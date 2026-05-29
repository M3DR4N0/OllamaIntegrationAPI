import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import {
  analyzeContract,
  analyzeLocalFile,
  healthCheck,
  listDocuments,
  queryDocuments
} from "./ragClient.js";

function formatToolResult(result: unknown): string {
  if (
    result &&
    typeof result === "object" &&
    "success" in result &&
    "rawText" in result &&
    (result as { rawText?: unknown }).rawText
  ) {
    return JSON.stringify(result, null, 2);
  }

  return JSON.stringify(result, null, 2);
}

function textResponse(text: string) {
  return {
    content: [
      {
        type: "text" as const,
        text
      }
    ]
  };
}

function buildExecutiveContextQuery(params: {
  tema: string;
  incluirRiesgos: boolean;
  incluirObligaciones: boolean;
  incluirFechasCriticas: boolean;
  incluirFuentes: boolean;
}): string {
  const lines = [
    `Genera contexto para un informe ejecutivo sobre: ${params.tema}.`,
    "Incluye resumen ejecutivo."
  ];

  if (params.incluirRiesgos) {
    lines.push("Incluye riesgos si aplica.");
  }

  if (params.incluirObligaciones) {
    lines.push("Incluye obligaciones si aplica.");
  }

  if (params.incluirFechasCriticas) {
    lines.push("Incluye fechas criticas si aplica.");
  }

  if (params.incluirFuentes) {
    lines.push("Incluye fuentes/documentos usados si estan disponibles.");
  }

  lines.push("Incluye preguntas pendientes si falta informacion.");

  return lines.join("\n");
}

export function registerTools(server: McpServer): void {
  server.tool(
    "health_check_rag",
    "Verifica si la API local RAG esta disponible usando GET /health y, si falla, GET /.",
    {},
    async () => {
      const result = await healthCheck();
      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "consultar_base_documental",
    "Consulta la base documental local mediante POST /api/query.",
    {
      pregunta: z.string().min(1, "pregunta es requerida"),
      topK: z.number().int().positive().default(5),
      model: z.string().min(1).default("gemma3:1b")
    },
    async ({ pregunta, topK, model }) => {
      const result = await queryDocuments({
        query: pregunta,
        model,
        topK
      });

      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "generar_informe_contextual",
    "Consulta la base documental y prepara contexto estructurado para que Claude genere un informe ejecutivo.",
    {
      tema: z.string().min(1, "tema es requerido"),
      incluirRiesgos: z.boolean().default(true),
      incluirObligaciones: z.boolean().default(true),
      incluirFechasCriticas: z.boolean().default(true),
      incluirFuentes: z.boolean().default(true),
      topK: z.number().int().positive().default(10),
      model: z.string().min(1).default("gemma3:1b")
    },
    async ({
      tema,
      incluirRiesgos,
      incluirObligaciones,
      incluirFechasCriticas,
      incluirFuentes,
      topK,
      model
    }) => {
      const query = buildExecutiveContextQuery({
        tema,
        incluirRiesgos,
        incluirObligaciones,
        incluirFechasCriticas,
        incluirFuentes
      });

      const result = await queryDocuments({
        query,
        model,
        topK
      });

      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "analizar_contrato_local",
    "Analiza contratos o documentos legales ya indexados en la base documental mediante POST /api/analysis/contract.",
    {
      pregunta: z.string().min(1, "pregunta es requerida"),
      topK: z.number().int().positive().default(8),
      model: z.string().min(1).default("gemma3:1b")
    },
    async ({ pregunta, topK, model }) => {
      const result = await analyzeContract({
        query: pregunta,
        model,
        topK
      });

      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "listar_documentos_locales",
    "Lista documentos indexados o disponibles mediante GET /api/agent/documents.",
    {},
    async () => {
      const result = await listDocuments();
      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "analizar_archivo_local",
    "Envia un archivo local a la API mediante multipart/form-data usando el campo exacto file.",
    {
      filePath: z.string().min(1, "filePath es requerido"),
      pregunta: z.string().min(1, "pregunta es requerida"),
      model: z.string().min(1).default("gemma3:1b"),
      topK: z.number().int().positive().default(8)
    },
    async ({ filePath, pregunta, model, topK }) => {
      const result = await analyzeLocalFile({
        filePath,
        query: pregunta,
        model,
        topK
      });

      return textResponse(formatToolResult(result));
    }
  );
}
