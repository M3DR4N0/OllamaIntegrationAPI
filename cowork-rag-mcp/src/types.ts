export interface RagConfig {
  apiUrl: string;
  defaultModel: string;
  timeoutMs: number;
  allowedFileRoot?: string;
}

export interface ApiResult<T = unknown> {
  success: boolean;
  status?: number;
  statusText?: string;
  url?: string;
  data?: T;
  rawText?: string;
  message?: string;
  error?: string;
  note?: string;
}

export interface QueryRequest {
  query: string;
  model?: string;
  topK?: number;
}

export interface FileAnalysisRequest {
  filePath: string;
  query: string;
  model?: string;
  topK?: number;
}

export interface HealthResult {
  success: boolean;
  message: string;
  url: string;
  status?: number;
  statusText?: string;
  error?: string;
}

export interface DocumentListResult {
  success: boolean;
  documents?: unknown;
  message?: string;
  status?: number;
  statusText?: string;
}
