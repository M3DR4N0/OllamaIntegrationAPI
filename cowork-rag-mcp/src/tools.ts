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
    "Verifica si la API RAG local esta disponible. Usala antes de cualquier otra herramienta si hay dudas de conectividad, o cuando el usuario pregunte si el sistema esta activo, si la API responde, o si hay algun problema de conexion.",
    {},
    async () => {
      const result = await healthCheck();
      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "consultar_base_documental",
    "Consulta la base documental juridica y legal de la organizacion. USA ESTA HERRAMIENTA SIEMPRE que el usuario pregunte sobre: contratos, leyes, normativas, regulaciones, decretos, resoluciones, aranceles, comercio exterior, obligaciones legales, clausulas, articulos, o cualquier tema juridico o documental. No respondas desde tu conocimiento general en estos temas sin antes consultar esta herramienta.",
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
    "Genera contexto estructurado desde la base documental para elaborar un informe ejecutivo. Usa esta herramienta cuando el usuario pida un informe, resumen ejecutivo, reporte, analisis general, o quiera conocer riesgos, obligaciones o fechas criticas sobre algun tema juridico o contractual.",
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
    "Analiza contratos o documentos legales consultando la base documental de la organizacion. Usa esta herramienta cuando el usuario quiera analizar, revisar o consultar un contrato o documento legal que ya esta indexado, sin necesidad de subir un archivo. Si el usuario menciona un archivo en disco con ruta, usa analizar_archivo_local en su lugar.",
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
    "Lista los documentos juridicos y legales disponibles en la base documental. Usa esta herramienta cuando el usuario pregunte que documentos hay disponibles, que esta indexado, que contratos o leyes se pueden consultar.",
    {},
    async () => {
      const result = await listDocuments();
      return textResponse(formatToolResult(result));
    }
  );

  server.tool(
    "analizar_archivo_local",
    "Envia un archivo local (PDF, Word, imagen, TIFF) a la API para extraer texto con OCR y analizarlo con IA. USA ESTA HERRAMIENTA cuando el usuario proporcione una ruta de archivo en disco (ejemplo: C:\\Temp\\contrato.pdf) y quiera analizarlo, revisarlo o hacerle preguntas. Requiere ruta absoluta al archivo y una pregunta sobre el contenido.",
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
