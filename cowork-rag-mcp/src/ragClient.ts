import { promises as fs } from "node:fs";
import path from "node:path";
import {
  ApiResult,
  DocumentListResult,
  FileAnalysisRequest,
  HealthResult,
  QueryRequest,
  RagConfig
} from "./types.js";

const DEFAULT_API_URL = "http://localhost:5000";
const DEFAULT_MODEL = "gemma3:1b";
const DEFAULT_TIMEOUT_MS = 60_000;

function normalizeBaseUrl(url: string): string {
  return url.replace(/\/+$/, "");
}

function getErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}

function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

function buildUrl(config: RagConfig, endpoint: string): string {
  const normalizedEndpoint = endpoint.startsWith("/") ? endpoint : `/${endpoint}`;
  return `${config.apiUrl}${normalizedEndpoint}`;
}

export function getConfig(): RagConfig {
  const rawTimeout = Number.parseInt(process.env.RAG_TIMEOUT_MS ?? "", 10);

  return {
    apiUrl: normalizeBaseUrl(process.env.RAG_API_URL || DEFAULT_API_URL),
    defaultModel: process.env.RAG_DEFAULT_MODEL || DEFAULT_MODEL,
    timeoutMs: Number.isFinite(rawTimeout) && rawTimeout > 0 ? rawTimeout : DEFAULT_TIMEOUT_MS,
    allowedFileRoot: process.env.RAG_ALLOWED_FILE_ROOT?.trim() || undefined
  };
}

export async function withTimeout<T>(
  timeoutMs: number,
  operation: (signal: AbortSignal) => Promise<T>
): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    return await operation(controller.signal);
  } finally {
    clearTimeout(timeout);
  }
}

export async function parseApiResponse<T = unknown>(
  response: Response
): Promise<ApiResult<T>> {
  const body = await response.text();
  const resultBase = {
    status: response.status,
    statusText: response.statusText,
    url: response.url
  };

  let parsed: T | undefined;
  let parseError: string | undefined;

  if (body.trim().length > 0) {
    try {
      parsed = JSON.parse(body) as T;
    } catch (error) {
      parseError = getErrorMessage(error);
    }
  }

  if (!response.ok) {
    return {
      success: false,
      ...resultBase,
      data: parsed,
      rawText: parsed === undefined ? body : undefined,
      error: `La API devolvio HTTP ${response.status} ${response.statusText}`,
      note: parseError ? `El cuerpo no fue JSON parseable: ${parseError}` : undefined
    };
  }

  if (parsed !== undefined) {
    return {
      success: true,
      ...resultBase,
      data: parsed
    };
  }

  return {
    success: true,
    ...resultBase,
    rawText: body,
    note: "La API respondio correctamente, pero no devolvio JSON parseable."
  };
}

export async function getJson<T = unknown>(endpoint: string): Promise<ApiResult<T>> {
  const config = getConfig();
  const url = buildUrl(config, endpoint);

  try {
    return await withTimeout(config.timeoutMs, async (signal) => {
      const response = await fetch(url, {
        method: "GET",
        headers: { Accept: "application/json, text/plain;q=0.9, */*;q=0.8" },
        signal
      });

      return parseApiResponse<T>(response);
    });
  } catch (error) {
    return {
      success: false,
      url,
      error: isAbortError(error)
        ? `Timeout despues de ${config.timeoutMs} ms al llamar ${url}`
        : `No se pudo conectar con la API local: ${getErrorMessage(error)}`
    };
  }
}

export async function postJson<T = unknown>(
  endpoint: string,
  payload: unknown
): Promise<ApiResult<T>> {
  const config = getConfig();
  const url = buildUrl(config, endpoint);

  try {
    return await withTimeout(config.timeoutMs, async (signal) => {
      const response = await fetch(url, {
        method: "POST",
        headers: {
          Accept: "application/json, text/plain;q=0.9, */*;q=0.8",
          "Content-Type": "application/json"
        },
        body: JSON.stringify(payload),
        signal
      });

      return parseApiResponse<T>(response);
    });
  } catch (error) {
    return {
      success: false,
      url,
      error: isAbortError(error)
        ? `Timeout despues de ${config.timeoutMs} ms al llamar ${url}`
        : `No se pudo conectar con la API local: ${getErrorMessage(error)}`
    };
  }
}

function getContentType(filePath: string): string {
  const extension = path.extname(filePath).toLowerCase();

  const contentTypes: Record<string, string> = {
    ".pdf": "application/pdf",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ".txt": "text/plain",
    ".tif": "image/tiff",
    ".tiff": "image/tiff",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg"
  };

  return contentTypes[extension] ?? "application/octet-stream";
}

async function validateLocalFile(filePath: string, config: RagConfig): Promise<string | ApiResult> {
  if (!filePath.trim()) {
    return {
      success: false,
      error: "filePath no puede estar vacio."
    };
  }

  const resolvedFilePath = path.resolve(filePath);
  let fileStat;

  try {
    fileStat = await fs.stat(resolvedFilePath);
  } catch (error) {
    return {
      success: false,
      error: `El archivo no existe o no se pudo acceder: ${resolvedFilePath}`,
      message: getErrorMessage(error)
    };
  }

  if (!fileStat.isFile()) {
    return {
      success: false,
      error: `La ruta no es un archivo: ${resolvedFilePath}`
    };
  }

  let realFilePath: string;

  try {
    realFilePath = await fs.realpath(resolvedFilePath);
  } catch (error) {
    return {
      success: false,
      error: `No se pudo resolver la ruta real del archivo: ${resolvedFilePath}`,
      message: getErrorMessage(error)
    };
  }

  if (config.allowedFileRoot) {
    const resolvedRoot = path.resolve(config.allowedFileRoot);
    let realRoot: string;

    try {
      const rootStat = await fs.stat(resolvedRoot);
      if (!rootStat.isDirectory()) {
        return {
          success: false,
          error: `RAG_ALLOWED_FILE_ROOT no es un directorio: ${resolvedRoot}`
        };
      }

      realRoot = await fs.realpath(resolvedRoot);
    } catch (error) {
      return {
        success: false,
        error: `No se pudo acceder a RAG_ALLOWED_FILE_ROOT: ${resolvedRoot}`,
        message: getErrorMessage(error)
      };
    }

    const relativePath = path.relative(realRoot, realFilePath);
    const isOutsideRoot =
      relativePath === "" ||
      relativePath.startsWith("..") ||
      path.isAbsolute(relativePath);

    if (isOutsideRoot) {
      return {
        success: false,
        error: `Archivo bloqueado por seguridad. La ruta debe estar dentro de RAG_ALLOWED_FILE_ROOT: ${realRoot}`,
        message: `Ruta solicitada: ${realFilePath}`
      };
    }
  }

  return realFilePath;
}

export async function postMultipartFile<T = unknown>(
  endpoint: string,
  request: FileAnalysisRequest
): Promise<ApiResult<T>> {
  const config = getConfig();
  const url = buildUrl(config, endpoint);
  const validatedPath = await validateLocalFile(request.filePath, config);

  if (typeof validatedPath !== "string") {
    return validatedPath as ApiResult<T>;
  }

  let fileBuffer: Buffer;

  try {
    fileBuffer = await fs.readFile(validatedPath);
  } catch (error) {
    return {
      success: false,
      error: `No se pudo leer el archivo: ${validatedPath}`,
      message: getErrorMessage(error)
    };
  }

  const fileName = path.basename(validatedPath);
  const contentType = getContentType(validatedPath);
  const fileBytes = new Uint8Array(fileBuffer.byteLength);
  fileBytes.set(fileBuffer);
  const formData = new FormData();

  formData.append("file", new Blob([fileBytes], { type: contentType }), fileName);
  formData.append("query", request.query);
  formData.append("model", request.model ?? config.defaultModel);
  formData.append("topK", String(request.topK ?? 8));

  try {
    return await withTimeout(config.timeoutMs, async (signal) => {
      const response = await fetch(url, {
        method: "POST",
        headers: { Accept: "application/json, text/plain;q=0.9, */*;q=0.8" },
        body: formData,
        signal
      });

      return parseApiResponse<T>(response);
    });
  } catch (error) {
    return {
      success: false,
      url,
      error: isAbortError(error)
        ? `Timeout despues de ${config.timeoutMs} ms al subir ${fileName} a ${url}`
        : `No se pudo enviar el archivo. Puede ser demasiado grande, ilegible o la API local no esta disponible: ${getErrorMessage(error)}`
    };
  }
}

export async function healthCheck(): Promise<HealthResult> {
  const config = getConfig();
  const health = await getJson("/health");

  if (health.success) {
    return {
      success: true,
      message: "API local disponible",
      url: config.apiUrl,
      status: health.status,
      statusText: health.statusText
    };
  }

  const root = await getJson("/");

  if (root.success) {
    return {
      success: true,
      message: "API local disponible",
      url: config.apiUrl,
      status: root.status,
      statusText: root.statusText
    };
  }

  return {
    success: false,
    message: "La API local no respondio en /health ni en /.",
    url: config.apiUrl,
    status: root.status ?? health.status,
    statusText: root.statusText ?? health.statusText,
    error: root.error ?? health.error
  };
}

export async function queryDocuments<T = unknown>(
  request: QueryRequest
): Promise<ApiResult<T>> {
  const config = getConfig();

  return postJson<T>("/api/query", {
    query: request.query,
    model: request.model ?? config.defaultModel,
    topK: request.topK ?? 5
  });
}

export async function analyzeContract<T = unknown>(
  request: QueryRequest
): Promise<ApiResult<T>> {
  const config = getConfig();

  return postJson<T>("/api/analysis/contract", {
    query: request.query,
    model: request.model ?? config.defaultModel,
    topK: request.topK ?? 8
  });
}

export async function listDocuments(): Promise<ApiResult<DocumentListResult | unknown>> {
  const result = await getJson<DocumentListResult | unknown>("/api/agent/documents");

  if (!result.success && result.status === 404) {
    return {
      success: true,
      status: 404,
      statusText: result.statusText,
      url: result.url,
      data: {
        success: false,
        status: 404,
        statusText: result.statusText,
        message: "El endpoint /api/agent/documents todavia no esta implementado en la API .NET."
      }
    };
  }

  return result;
}

export async function analyzeLocalFile<T = unknown>(
  request: FileAnalysisRequest
): Promise<ApiResult<T>> {
  const config = getConfig();

  return postMultipartFile<T>("/api/analysis/contract", {
    filePath: request.filePath,
    query: request.query,
    model: request.model ?? config.defaultModel,
    topK: request.topK ?? 8
  });
}
