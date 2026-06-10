# 📘 OllamaIntegrationAPI — Documentación Completa

> **Plataforma de IA** basada en .NET 9 con pipeline RAG, análisis de contratos y orquestación inteligente de consultas, integrada con **Ollama** (LLM local) y **Qdrant** (base de datos vectorial).

---

## 📑 Tabla de Contenidos

1. [Visión General](#1-visión-general)
2. [Arquitectura](#2-arquitectura)
3. [Estructura de Carpetas](#3-estructura-de-carpetas)
4. [Infraestructura y Despliegue](#4-infraestructura-y-despliegue)
5. [Configuración y Variables de Entorno](#5-configuración-y-variables-de-entorno)
6. [Dependencias (NuGet)](#6-dependencias-nuget)
7. [Endpoints de la API](#7-endpoints-de-la-api)
8. [Servicios — Interfaces](#8-servicios--interfaces)
9. [Servicios — Implementaciones](#9-servicios--implementaciones)
10. [Modelos de Datos](#10-modelos-de-datos)
11. [Helpers / Utilidades](#11-helpers--utilidades)
12. [Pipelines de Procesamiento](#12-pipelines-de-procesamiento)
13. [Registro de Dependencias (DI)](#13-registro-de-dependencias-di)
14. [Guía de Uso Rápido](#14-guía-de-uso-rápido)

---

## 1. Visión General

**OllamaIntegrationAPI** es una API REST que expone capacidades de:

| Capacidad | Descripción |
|---|---|
| **Extracción de documentos** | Extrae texto de PDF, Word, imágenes y TIFF (con OCR vía Tesseract). |
| **Ingesta RAG** | Parsea documentos legales/regulatorios, los divide en chunks semánticos y los almacena como embeddings en Qdrant. |
| **Consulta RAG** | Recibe una pregunta, busca contexto relevante en Qdrant y genera una respuesta fundamentada con el LLM. |
| **Análisis de contratos** | Compara un contrato subido contra la base de conocimiento legal almacenada en Qdrant, identificando riesgos y cumplimiento. |
| **Orquestación inteligente** | Clasifica la intención del usuario (análisis, consulta legal, consulta general, datos) y enruta al pipeline adecuado. |

**Stack tecnológico:**
- Runtime: **.NET 9** (C# 13.0), ASP.NET Core Web API
- LLM: **Ollama** (local, modelos como `llama3`)
- Embeddings: **Ollama** `/api/embed` (modelo `nomic-embed-text`, 768 dimensiones)
- Base vectorial: **Qdrant** (gRPC, puerto 6334)
- OCR: **Tesseract** 5.2.0 (español)
- Contenedores: **Docker Compose**

---

## 2. Arquitectura

```
┌─────────────────────────────────────────────────────────────────┐
│                        CLIENTES (HTTP)                          │
└──────────────┬──────────────────────────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────────────────────────┐
│                     ASP.NET Core Web API                        │
│  ┌──────────────┐ ┌──────────────┐ ┌────────────┐ ┌──────────┐ │
│  │ Document     │ │ Ingestion    │ │ Analysis   │ │ Query    │ │
│  │ Controller   │ │ Controller   │ │ Controller │ │ Controller│ │
│  └──────┬───────┘ └──────┬───────┘ └─────┬──────┘ └────┬─────┘ │
│         │                │               │              │       │
│  ┌──────▼────────────────▼───────────────▼──────────────▼─────┐ │
│  │                   CAPA DE SERVICIOS                        │ │
│  │  ┌─────────────────┐  ┌──────────────┐  ┌───────────────┐ │ │
│  │  │ DocumentParser   │  │ Chunking     │  │ Embedding     │ │ │
│  │  │ Service          │  │ Service      │  │ Service       │ │ │
│  │  └─────────────────┘  └──────────────┘  └───────────────┘ │ │
│  │  ┌─────────────────┐  ┌──────────────┐  ┌───────────────┐ │ │
│  │  │ VectorStore      │  │ LLM          │  │ Analysis      │ │ │
│  │  │ Service (Qdrant) │  │ Service      │  │ Service       │ │ │
│  │  └─────────────────┘  └──────────────┘  └───────────────┘ │ │
│  │  ┌─────────────────┐                                      │ │
│  │  │ Orchestrator     │                                      │ │
│  │  │ Service          │                                      │ │
│  │  └─────────────────┘                                      │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │                      HELPERS                               │ │
│  │  JsonSanitizer │ ContextBuilder │ VectorMath │ Validator   │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
               │                    │
               ▼                    ▼
        ┌──────────┐         ┌──────────┐
        │  Ollama  │         │  Qdrant  │
        │ :11434   │         │ :6334    │
        └──────────┘         └──────────┘
```

### Flujos principales

```
INGESTA:     Archivo → Parser → Chunker → Embedder → Qdrant (persistido)
CONSULTA:    Pregunta → Embedder → Qdrant Search → ContextBuilder → LLM → Respuesta
ANÁLISIS:    Contrato → Parser → Chunker → Rank(embed) + Qdrant Legal → LLM → AnalysisResult
EXTRACCIÓN:  Archivo → DocumentProcessor → Chunker → Rank(embed) + Qdrant Legal → LLM → Respuesta
```

---

## 3. Estructura de Carpetas

```
LlamaIntegrationAPI/
├── Controllers/
│   ├── DocumentController.cs      # Extracción de documentos (RAG-enhanced)
│   ├── IngestionController.cs     # Ingesta de documentos legales a Qdrant
│   ├── AnalysisController.cs      # Análisis de contratos
│   ├── QueryController.cs         # Consultas RAG + orquestador
│   └── InnerController.cs         # Health check / ping / version
│
├── Services/
│   ├── Interfaces/
│   │   ├── IDocumentParserService.cs
│   │   ├── IChunkingService.cs
│   │   ├── IEmbeddingService.cs
│   │   ├── IVectorStoreService.cs
│   │   ├── ILLMService.cs
│   │   ├── IAnalysisService.cs
│   │   └── IOrchestratorService.cs
│   │
│   ├── Implementations/
│   │   ├── DocumentParserService.cs    # Delega a DocumentProcessor
│   │   ├── ChunkingService.cs          # Chunking semántico + token fallback
│   │   ├── EmbeddingService.cs         # Ollama /api/embed
│   │   ├── QdrantVectorStoreService.cs # Qdrant gRPC client
│   │   ├── LLMService.cs              # Ollama /api/generate
│   │   ├── AnalysisService.cs         # Pipeline de análisis de contratos
│   │   └── OrchestratorService.cs     # Clasificación de intención + routing
│   │
│   ├── DocumentProcessor.cs           # Extracción OCR (PDF, Word, imagen)
│   └── OllamaService.cs              # Cliente HTTP legacy para Ollama
│
├── Models/
│   ├── Rag/
│   │   ├── ChunkMetadata.cs
│   │   ├── DocumentChunk.cs
│   │   ├── IngestionRequest.cs
│   │   ├── AnalysisRequest.cs
│   │   ├── AnalysisResult.cs
│   │   └── QueryRequest.cs
│   │
│   ├── OllamaRequest.cs              # ExtractFromFileRequest
│   └── Response/
│       ├── Response.cs                # IResponse, ResponseSuccess, ResponseError
│       └── ResponseHandler.cs
│
├── Helpers/
│   ├── JsonSanitizer.cs              # 6 estrategias de extracción JSON
│   ├── ContextBuilder.cs             # Ensamblaje de prompts con contexto
│   ├── VectorMath.cs                 # Similitud coseno
│   ├── PayloadBuilder.cs             # Builder de payloads legacy
│   ├── RequestValidator.cs           # Validación de requests
│   └── TextChunker.cs               # Chunker legacy por tokens
│
├── Middlewares/
│   └── ErrorHandlerMiddleware.cs     # Manejo global de errores
│
├── tessdata/                          # Datos de Tesseract (español)
├── Program.cs                        # Bootstrap + DI
├── Dockerfile
└── LlamaIntegrationAPI.csproj

docker-compose.yml                     # API + Ollama + Qdrant
docker-compose.override.yml            # Override intencionalmente vacio
docker-compose.dcproj
.env                                   # Variables sensibles
.gitignore
```

---

## 4. Infraestructura y Despliegue

### Docker Compose

El sistema se despliega con **3 contenedores**:

```yaml
services:
  llamaintegrationapi:       # API .NET 9
    ports: ["5000:8080"]
    depends_on: [ollama, qdrant]
    volumes:
      - ./LlamaIntegrationAPI/tessdata:/app/tessdata:ro  # OCR data

  ollama:                    # Servidor LLM local
    image: ollama/ollama:latest
    ports: ["11434:11434"]
    volumes:
      - ollama_models:/root/.ollama  # Modelos persistentes

  qdrant:                    # Base de datos vectorial
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"          # REST API
      - "6334:6334"          # gRPC (usado por la app)
    volumes:
      - qdrant_data:/qdrant/storage
```

### Comandos de despliegue

```bash
# Levantar todos los servicios
docker-compose up -d

# Ver logs
docker-compose logs -f llamaintegrationapi

# Descargar un modelo en Ollama (ejecutar una vez)
docker exec -it <ollama_container> ollama pull llama3
docker exec -it <ollama_container> ollama pull nomic-embed-text

# Rebuild solo la API
docker-compose up -d --build llamaintegrationapi
```

---

## 5. Configuración y Variables de Entorno

| Variable | Default | Descripción |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` | URL de escucha de Kestrel |
| `OLLAMA_HOST` | `http://localhost:11434` | URL del servidor Ollama |
| `QDRANT_HOST` | `localhost` | Host del servidor Qdrant |
| `QDRANT_PORT` | `6334` | Puerto gRPC de Qdrant |
| `EMBEDDING_MODEL` | `nomic-embed-text` | Modelo de embeddings en Ollama |
| `EMBEDDING_DIMENSIONS` | `768` | Dimensiones del vector de embeddings |

### Configuración de Kestrel

El servidor está configurado para aceptar archivos grandes:

```csharp
// Límite de cuerpo: long.MaxValue
// Límite de headers: 1 GB
// Buffer de request/response: 1 GB
// MultipartBodyLengthLimit: long.MaxValue
```

---

## 6. Dependencias (NuGet)

| Paquete | Versión | Uso |
|---|---|---|
| `Qdrant.Client` | 1.13.0 | Cliente gRPC para Qdrant |
| `OllamaSharp` | 5.4.4 | Modelos de request/response para Ollama |
| `SharpToken` | 2.0.3 | Tokenización (cl100k_base) para chunking |
| `UglyToad.PdfPig` | 1.7.0-custom-5 | Extracción de texto de PDF |
| `DocX` | 4.0.x | Parsing de documentos Word |
| `Tesseract` | 5.2.0 | OCR para imágenes y PDFs escaneados |
| `Magick.NET-Q16-AnyCPU` | 14.7.0 | Procesamiento de imágenes (TIFF multi-frame) |
| `SixLabors.ImageSharp` | 3.1.11 | Procesamiento de imágenes adicional |
| `System.Drawing.Common` | 9.0.8 | Soporte gráfico |
| `Microsoft.AspNetCore.OpenApi` | 9.0.5 | Documentación OpenAPI |

---

## 7. Endpoints de la API

### 7.1 `DocumentController` — `/api/document`

#### `POST /api/document/extract-file`

Extrae información de un documento usando el LLM con contexto RAG.

| Parámetro | Tipo | Descripción |
|---|---|---|
| `File` | `IFormFile` | Archivo principal (PDF, Word, imagen) |
| `TiffFile` | `IFormFile[]` | Archivos TIFF opcionales |
| `Prompt` | `string` | Instrucción/pregunta sobre el documento |
| `Model` | `string` | Modelo de Ollama (ej. `llama3`) |
| `Format` | `string?` | Formato de respuesta (ej. `json`) |

**Flujo interno:**
1. Extrae texto del documento (OCR si es necesario)
2. Divide en chunks semánticos
3. Rankea chunks por relevancia (cosine similarity) — máx. 10
4. Recupera contexto legal de Qdrant — top 5
5. Construye prompt enriquecido con `ContextBuilder`
6. Envía al LLM y retorna la respuesta

**Ejemplo `curl`:**
```bash
curl -X POST http://localhost:5000/api/document/extract-file \
  -F "File=@contrato.pdf" \
  -F "Prompt=Resume las cláusulas principales" \
  -F "Model=llama3"
```

---

### 7.2 `IngestionController` — `/api/ingestion`

#### `POST /api/ingestion/upload`

Ingesta un documento legal/regulatorio en la base vectorial.

| Parámetro | Tipo | Descripción |
|---|---|---|
| `File` | `IFormFile` | Documento a ingestar (PDF, Word, imagen) |
| `DocumentType` | `string` | Tipo de documento (ej. `ley`, `regulación`) |
| `Source` | `string` | Fuente (ej. `Diario Oficial`, `SUNAT`) |

**Flujo interno:**
1. Extrae texto con `IDocumentParserService`
2. Divide en chunks con `IChunkingService`
3. Genera embeddings en batch con `IEmbeddingService`
4. Crea colección `legal_documents` en Qdrant (si no existe)
5. Almacena chunks + embeddings via `IVectorStoreService.UpsertAsync`

**Respuesta exitosa:**
```json
{
  "statusCode": 200,
  "data": { "chunksIngested": 45 }
}
```

**Ejemplo `curl`:**
```bash
curl -X POST http://localhost:5000/api/ingestion/upload \
  -F "File=@ley_aduanas.pdf" \
  -F "DocumentType=ley" \
  -F "Source=Diario Oficial"
```

---

### 7.3 `AnalysisController` — `/api/analysis`

#### `POST /api/analysis/contract`

Analiza un contrato contra la base de conocimiento legal.

| Parámetro | Tipo | Descripción |
|---|---|---|
| `ContractFile` | `IFormFile` | Contrato a analizar |
| `Query` | `string` | Pregunta o enfoque del análisis |
| `Model` | `string` | Modelo LLM (default: `llama3`) |
| `TopK` | `int` | Número de chunks legales a recuperar (default: `5`) |

**Respuesta:**
```json
{
  "statusCode": 200,
  "data": {
    "compliance": false,
    "risks": [
      "Cláusula 5 no especifica penalidades conforme al Art. 23",
      "Falta referencia al tipo de cambio según regulación vigente"
    ],
    "relatedArticles": [
      "Artículo 23 - Ley de Comercio Exterior",
      "Sección 4.2 - Regulación Cambiaria"
    ],
    "summary": "El contrato presenta 2 riesgos principales..."
  }
}
```

**Ejemplo `curl`:**
```bash
curl -X POST http://localhost:5000/api/analysis/contract \
  -F "ContractFile=@contrato_importacion.pdf" \
  -F "Query=Verifica cumplimiento con normativa aduanera" \
  -F "Model=llama3" \
  -F "TopK=10"
```

---

### 7.4 `QueryController` — `/api/query`

#### `POST /api/query`

Consulta RAG basada en texto contra la base de conocimiento legal.

**Body (JSON):**
```json
{
  "query": "¿Cuáles son los requisitos para importar alimentos?",
  "model": "llama3",
  "topK": 5
}
```

**Respuesta:**
```json
{
  "statusCode": 200,
  "data": {
    "answer": "Según el Artículo 15 de la Ley...",
    "sources": [
      {
        "document": "ley_aduanas.pdf",
        "section": "Capítulo III",
        "article": "Artículo 15"
      }
    ]
  }
}
```

#### `POST /api/query/with-file`

Consulta con archivo adjunto — se enruta automáticamente a análisis de contratos.

| Parámetro | Tipo | Descripción |
|---|---|---|
| `query` | `string` (form) | Pregunta del usuario |
| `model` | `string` (form) | Modelo LLM |
| `topK` | `int` (form) | Chunks a recuperar |
| `file` | `IFormFile` (form) | Archivo opcional |

---

### 7.5 `InnerController` — `/api/inner`

| Endpoint | Método | Descripción |
|---|---|---|
| `/api/inner/ping` | GET | Health check (`pong`) |
| `/api/inner/version` | GET | Versión de la API |

---

## 8. Servicios — Interfaces

### `IDocumentParserService`
```csharp
Task<string> ExtractTextAsync(IFormFile file);
```
Extrae texto plano de cualquier archivo soportado (PDF, Word, imagen, TIFF).

### `IChunkingService`
```csharp
IReadOnlyList<DocumentChunk> Chunk(string text, ChunkMetadata baseMetadata);
```
Divide texto en chunks con metadatos. Síncrono (no hay I/O involucrado).

### `IEmbeddingService`
```csharp
int Dimensions { get; }
Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct);
Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct);
```
Genera embeddings vectoriales. Soporta batch para eficiencia.

### `IVectorStoreService`
```csharp
Task EnsureCollectionAsync(string collectionName, int vectorSize, CancellationToken ct);
Task UpsertAsync(string collectionName, IEnumerable<DocumentChunk> chunks, CancellationToken ct);
Task<IReadOnlyList<DocumentChunk>> SearchAsync(string collectionName, float[] queryVector, int topK, CancellationToken ct);
```
Abstracción sobre la base de datos vectorial (Qdrant).

### `ILLMService`
```csharp
Task<string> GenerateAsync(string systemPrompt, string userPrompt, string model, CancellationToken ct);
Task<T?> GenerateAsync<T>(string systemPrompt, string userPrompt, string model, CancellationToken ct) where T : class;
```
Abstracción sobre el LLM. El overload genérico solicita `format: "json"` y deserializa con `JsonSanitizer`.

### `IAnalysisService`
```csharp
Task<AnalysisResult> AnalyzeContractAsync(AnalysisRequest request, CancellationToken ct);
```
Pipeline completo de análisis de contratos.

### `IOrchestratorService`
```csharp
Task<IResponse> HandleAsync(string query, string model, IFormFile? file = null, int topK = 5, CancellationToken ct = default);
```
Clasifica intención y enruta al pipeline correcto.

---

## 9. Servicios — Implementaciones

### 9.1 `DocumentParserService`

**Ciclo de vida DI:** `Scoped`

Wrapper limpio sobre `DocumentProcessor`. Convierte un `IFormFile` en un `ExtractFromFileRequest` interno y delega la extracción.

### 9.2 `ChunkingService`

**Ciclo de vida DI:** `Singleton`

Divide documentos en fragmentos optimizados para embedding y retrieval.

#### Estrategia de dos fases:

**Fase 1 — Chunking Semántico:**
- Detecta estructura legal mediante regex (compilado, case-insensitive)
- Patrones soportados (español + inglés):

| Patrón | Ejemplo |
|---|---|
| `Artículo N` / `Article N` / `Art. N` | Artículo 23, Article IV |
| `Sección N` / `Section N` | Sección 4, Section III |
| `Capítulo N` / `Chapter N` | Capítulo I, Chapter 2 |
| `Título N` / `Title N` | Título II |
| `Cláusula N` / `Clause N` | Cláusula 3 |
| `Disposición (transitoria) N` | Disposición transitoria 1 |
| `Anexo N` / `Annex N` | Anexo A, Annex I |

- Requiere mínimo **2 encabezados** para activar el modo semántico
- El texto antes del primer encabezado se clasifica como "Preámbulo"
- Los segmentos que exceden **400 tokens** se sub-dividen preservando metadatos del encabezado

**Fase 2 — Fallback por Tokens:**
- Ventana deslizante: **400 tokens** con **50 tokens** de overlap
- Tokenizador: `cl100k_base` (SharpToken)
- Se usa cuando el documento no tiene estructura semántica detectada

#### Metadatos generados:
- `Section`: nombre de la sección/capítulo/cláusula (o "Chunk N/M" en fallback)
- `Article`: número de artículo (solo cuando el encabezado es un artículo)

### 9.3 `EmbeddingService`

**Ciclo de vida DI:** `HttpClient` (via `AddHttpClient`)

- Endpoint: Ollama `/api/embed`
- Modelo configurable: `EMBEDDING_MODEL` (default: `nomic-embed-text`)
- Dimensiones: `EMBEDDING_DIMENSIONS` (default: `768`)
- Soporta generación en **batch** (envía array de textos en una sola llamada)
- Timeout: **10 minutos**

### 9.4 `QdrantVectorStoreService`

**Ciclo de vida DI:** `Singleton`

- Protocolo: **gRPC** (puerto 6334)
- Distancia: **Cosine Similarity**
- Colección principal: `legal_documents`

#### Operaciones:

| Método | Descripción |
|---|---|
| `EnsureCollectionAsync` | Crea la colección si no existe (idempotente) |
| `UpsertAsync` | Inserta chunks en batches de **256 puntos** |
| `SearchAsync` | Búsqueda por similitud coseno, retorna top-K |

#### Campos del payload en Qdrant:

| Campo | Tipo | Descripción |
|---|---|---|
| `text` | string | Contenido textual del chunk |
| `document_name` | string | Nombre del archivo original |
| `document_type` | string | Tipo de documento |
| `section` | string? | Sección o capítulo |
| `article` | string? | Número de artículo |
| `source` | string | Fuente del documento |

### 9.5 `LLMService`

**Ciclo de vida DI:** `HttpClient` (via `AddHttpClient`)

- Endpoint: Ollama `/api/generate`
- Temperatura: **0** (determinista)
- Timeout: **1 hora**
- Streaming: **deshabilitado** (respuesta completa)

#### Dos modos de operación:

| Método | Formato | Uso |
|---|---|---|
| `GenerateAsync(...)` | Texto libre | Consultas generales, RAG |
| `GenerateAsync<T>(...)` | `format: "json"` | Análisis estructurado (deserializa vía `JsonSanitizer`) |

### 9.6 `AnalysisService`

**Ciclo de vida DI:** `Scoped`

Pipeline completo de análisis de contratos:

```
Contrato (IFormFile)
    │
    ▼
┌──────────────────┐
│ ExtractTextAsync  │  ← IDocumentParserService
└────────┬─────────┘
         ▼
┌──────────────────┐
│ Chunk (in-memory)│  ← IChunkingService (NO se almacena en Qdrant)
└────────┬─────────┘
         ▼
┌──────────────────┐
│ RankByRelevance  │  ← IEmbeddingService + VectorMath.CosineSimilarity
│ (máx. 12 chunks) │     Embed query + embed chunks → top-K
└────────┬─────────┘
         ▼
┌──────────────────┐
│ RetrieveLegal    │  ← IVectorStoreService.SearchAsync (graceful)
│ Context          │     Si Qdrant está vacío → lista vacía, no error
└────────┬─────────┘
         ▼
┌──────────────────┐
│ ContextBuilder   │  Combina chunks del contrato + chunks legales
│ .Build()         │  en un prompt estructurado
└────────┬─────────┘
         ▼
┌──────────────────┐
│ LLM GenerateAsync│  ← ILLMService.GenerateAsync<AnalysisResult>()
│ <AnalysisResult> │     System prompt: experto legal
└────────┬─────────┘
         ▼
    AnalysisResult { compliance, risks[], relatedArticles[], summary }
```

**System Prompt del análisis:**
- Rol: Experto legal en comercio internacional
- Entrada: fragmentos del contrato + extractos legales/regulatorios
- Tareas: verificar cumplimiento, identificar riesgos, citar artículos específicos
- Reglas: basarse SOLO en el texto proporcionado, no inventar información
- Idioma: mismo que el contrato
- Fallback: si la deserialización falla, reintenta como texto plano

### 9.7 `OrchestratorService`

**Ciclo de vida DI:** `Scoped`

Clasificador de intención basado en reglas con enrutamiento a pipelines especializados.

#### Clasificación de intención:

```
¿Tiene archivo adjunto?
    │
    ├─ SÍ → ContractAnalysis (delega a IAnalysisService)
    │
    └─ NO → ¿Contiene keywords de datos?
              │
              ├─ SÍ → DataQuery (501 Not Implemented - futuro)
              │
              └─ NO → ¿Contiene keywords legales?
                        │
                        ├─ SÍ → LegalRag (embed → search → LLM)
                        │
                        └─ NO → General (intenta enriquecer con legal si hay)
```

#### Keywords por intención:

| Intención | Keywords (ES + EN) |
|---|---|
| **ContractAnalysis** | contract, contrato, analyze, analizar, análisis, compliance, cumplimiento, clause, cláusula, review |
| **LegalRag** | law, ley, regulation, regulación, normativa, article, artículo, decreto, legal, trade, comercio, tariff, arancel, customs, aduana, treaty, tratado, statutory |
| **DataQuery** | total, sum, count, average, aggregate, how many, cuántos, cuánto, promedio, suma |

#### System Prompts por intención:

- **LegalRag:** "Eres un asistente de conocimiento legal especializado en comercio internacional..."
- **General:** "Eres un asistente útil con experiencia en análisis de documentos legales y financieros..."

#### Formato de respuesta RAG:
```json
{
  "answer": "Según el Artículo 15...",
  "sources": [
    { "document": "ley.pdf", "section": "Cap. III", "article": "Art. 15" }
  ]
}
```

---

## 10. Modelos de Datos

### 10.1 Modelos RAG (`Models/Rag/`)

#### `ChunkMetadata`
```csharp
record ChunkMetadata
{
    string DocumentName   // Nombre del archivo original
    string DocumentType   // Tipo MIME o categoría
    string? Section       // Sección/capítulo detectado
    string? Article       // Artículo detectado
    string Source          // Fuente del documento
}
```

#### `DocumentChunk`
```csharp
record DocumentChunk
{
    Guid Id               // Identificador único (auto-generado)
    string Text           // Contenido textual del chunk
    float[]? Embedding    // Vector de embedding (nullable hasta que se genera)
    ChunkMetadata Metadata
}
```

#### `IngestionRequest`
```csharp
class IngestionRequest
{
    IFormFile File         // Archivo a ingestar
    string DocumentType    // Tipo de documento
    string Source          // Fuente
}
```

#### `AnalysisRequest`
```csharp
class AnalysisRequest
{
    IFormFile ContractFile // Contrato a analizar
    string Query           // Pregunta o enfoque
    string Model = "llama3"
    int TopK = 5           // Chunks legales a recuperar
}
```

#### `AnalysisResult`
```csharp
class AnalysisResult
{
    bool Compliance                   // ¿Cumple con la normativa?
    List<string> Risks                // Riesgos identificados
    List<string> RelatedArticles      // Artículos relevantes citados
    string Summary                    // Resumen del análisis
}
```

#### `QueryRequest`
```csharp
class QueryRequest
{
    string Query           // Pregunta del usuario
    string Model = "llama3"
    int TopK = 5
}
```

### 10.2 Modelos Legacy

#### `ExtractFromFileRequest` (extiende `GenerateRequest` de OllamaSharp)
- `IFormFile? File` — Archivo principal
- `IFormFile[]? TiffFile` — Archivos TIFF
- `string Prompt` — Instrucción para el LLM

### 10.3 Modelos de Respuesta

#### `IResponse`
```csharp
interface IResponse
{
    HttpStatusCode StatusCode
    object? Data
    string? Error
}
```

- `ResponseHandler.Success(data)` → `ResponseSuccess` (200)
- `ResponseHandler.Error(message)` → `ResponseError` (500)
- `ResponseHandler.Success(data, statusCode)` → código personalizado

---

## 11. Helpers / Utilidades

### 11.1 `JsonSanitizer`

Extractor robusto de JSON para salidas de LLM que pueden contener formato no estándar.

#### 6 estrategias (en orden):

| # | Estrategia | Qué resuelve |
|---|---|---|
| 1 | Parse directo | JSON ya válido |
| 2 | Strip markdown fences | ` ```json ... ``` ` |
| 3 | Extracción balanceada `{}` | JSON envuelto en texto extra |
| 4 | Extracción balanceada `[]` | Arrays JSON envueltos en texto |
| 5 | Fix de problemas comunes | Trailing commas, comillas simples |
| 6 | Fix + extracción combinada | Combinación de 3 y 5 |

**Características:**
- Respeta strings literales (no rompe con `{` o `}` dentro de strings)
- `AllowTrailingCommas: true`
- `CommentHandling: JsonCommentHandling.Skip`
- `PropertyNameCaseInsensitive: true`
- Método genérico `TryExtractJson<T>()` para deserialización tipada

### 11.2 `ContextBuilder`

Ensambla prompts estructurados combinando chunks del documento y contexto legal.

**Formato del prompt generado:**
```
<pregunta del usuario>

=== CONTENIDO RELEVANTE DEL DOCUMENTO ===

[Artículo 23]
<texto del chunk>

[Sección 4]
<texto del chunk>

=== CONTEXTO LEGAL / REGULATORIO RELEVANTE ===

[Fuente: ley_aduanas.pdf — Artículo 15]
<texto del chunk legal>
```

### 11.3 `VectorMath`

```csharp
static float CosineSimilarity(float[] a, float[] b)
```

- Fórmula: `dot(a, b) / (||a|| * ||b||)`
- Retorna `0` si algún vector tiene magnitud cero
- Usado para rankear chunks por relevancia cuando hay más de los máximos permitidos

### 11.4 `RequestValidator`

Valida `ExtractFromFileRequest`:
- Verifica que `File` o `TiffFile` no sean nulos
- Verifica que `Prompt` no esté vacío
- Verifica que `Model` no esté vacío

---

## 12. Pipelines de Procesamiento

### 12.1 Pipeline de Ingesta

```
Usuario sube documento legal
         │
         ▼
[IDocumentParserService.ExtractTextAsync]
   PDF → PdfPig (+ Tesseract OCR si es escaneado)
   Word → DocX (+ Tesseract OCR si tiene imágenes)
   Imagen → Magick.NET + Tesseract OCR
   TIFF → Magick.NET (multi-frame) + Tesseract OCR
         │
         ▼ texto plano
[IChunkingService.Chunk]
   Intenta chunking semántico (regex de artículos/secciones)
   Si < 2 encabezados → fallback token-based (400 tokens, 50 overlap)
         │
         ▼ List<DocumentChunk>
[IEmbeddingService.GenerateEmbeddingsAsync]
   Batch de textos → Ollama /api/embed → float[768][]
         │
         ▼ chunks con embeddings
[IVectorStoreService.EnsureCollectionAsync + UpsertAsync]
   Crea colección "legal_documents" si no existe
   Inserta en Qdrant en batches de 256
         │
         ▼
   ✅ { chunksIngested: N }
```

### 12.2 Pipeline de Consulta RAG

```
Usuario envía pregunta
         │
         ▼
[IOrchestratorService.ClassifyIntent]
   Clasifica como LegalRag / ContractAnalysis / DataQuery / General
         │
         ▼ (LegalRag)
[IEmbeddingService.GenerateEmbeddingAsync]
   Embed de la pregunta → float[768]
         │
         ▼
[IVectorStoreService.SearchAsync]
   Busca top-K chunks más similares en "legal_documents"
         │
         ▼ List<DocumentChunk>
[ContextBuilder.Build]
   Combina pregunta + chunks legales en prompt estructurado
         │
         ▼
[ILLMService.GenerateAsync]
   System prompt: experto legal
   User prompt: contexto + pregunta
         │
         ▼
   { answer: "...", sources: [...] }
```

### 12.3 Pipeline de Análisis de Contratos

```
Usuario sube contrato + pregunta
         │
         ▼
[IDocumentParserService.ExtractTextAsync]
   Extrae texto del contrato
         │
         ▼
[IChunkingService.Chunk]
   Divide en chunks (IN-MEMORY, nunca se almacena)
         │
         ▼
[RankChunksByRelevance]
   Si > 12 chunks: embed query + embed chunks → cosine → top 12
   Si ≤ 12: usar todos
         │
         ▼
[IVectorStoreService.SearchAsync] (graceful)
   Busca contexto legal relevante en Qdrant
   Si falla o está vacío → continúa sin contexto legal
         │
         ▼
[ContextBuilder.Build]
   Chunks del contrato + chunks legales → prompt estructurado
         │
         ▼
[ILLMService.GenerateAsync<AnalysisResult>]
   format: "json" → JsonSanitizer deserializa
   Fallback: si falla, reintenta como texto plano
         │
         ▼
   AnalysisResult { compliance, risks, relatedArticles, summary }
```

---

## 13. Registro de Dependencias (DI)

```csharp
// Legacy (mantenidos)
builder.Services.AddScoped<IDocumentProcessor, DocumentProcessor>();
builder.Services.AddScoped<IPayloadBuilder, PayloadBuilder>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>();

// RAG Pipeline
builder.Services.AddScoped<IDocumentParserService, DocumentParserService>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>();
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();
builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>();
builder.Services.AddHttpClient<ILLMService, LLMService>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<IOrchestratorService, OrchestratorService>();
```

**Justificación de ciclos de vida:**

| Servicio | Lifetime | Razón |
|---|---|---|
| `ChunkingService` | Singleton | Sin estado, thread-safe, regex compilado |
| `QdrantVectorStoreService` | Singleton | `QdrantClient` es thread-safe |
| `EmbeddingService` | HttpClient | Gestión de `HttpClient` vía factory |
| `LLMService` | HttpClient | Gestión de `HttpClient` vía factory |
| `AnalysisService` | Scoped | Depende de servicios scoped |
| `OrchestratorService` | Scoped | Depende de servicios scoped |

---

## 14. Guía de Uso Rápido

### Paso 1: Levantar la infraestructura

```bash
docker-compose up -d
```

### Paso 2: Descargar modelos de Ollama

```bash
# Modelo de lenguaje
docker exec -it $(docker ps -qf "ancestor=ollama/ollama") ollama pull llama3

# Modelo de embeddings
docker exec -it $(docker ps -qf "ancestor=ollama/ollama") ollama pull nomic-embed-text
```

### Paso 3: Ingestar documentos legales

```bash
# Subir una ley o regulación
curl -X POST http://localhost:5000/api/ingestion/upload \
  -F "File=@ley_comercio_exterior.pdf" \
  -F "DocumentType=ley" \
  -F "Source=Congreso de la República"

# Subir más documentos para enriquecer la base
curl -X POST http://localhost:5000/api/ingestion/upload \
  -F "File=@regulacion_aduanera.pdf" \
  -F "DocumentType=regulacion" \
  -F "Source=SUNAT"
```

### Paso 4: Hacer consultas RAG

```bash
# Consulta sobre normativa
curl -X POST http://localhost:5000/api/query \
  -H "Content-Type: application/json" \
  -d '{
    "query": "¿Cuáles son los requisitos para importar alimentos según la normativa vigente?",
    "model": "llama3",
    "topK": 5
  }'
```

### Paso 5: Analizar un contrato

```bash
# Análisis de cumplimiento
curl -X POST http://localhost:5000/api/analysis/contract \
  -F "ContractFile=@contrato_importacion.pdf" \
  -F "Query=Verifica si este contrato cumple con la normativa aduanera vigente" \
  -F "Model=llama3" \
  -F "TopK=10"
```

### Paso 6: Extracción inteligente de documentos

```bash
# Extraer información específica de un documento
curl -X POST http://localhost:5000/api/document/extract-file \
  -F "File=@factura_comercial.pdf" \
  -F "Prompt=Extrae el número de factura, fecha, monto total y productos listados en formato JSON" \
  -F "Model=llama3" \
  -F "Format=json"
```

---

> **Nota:** Esta documentación refleja el estado actual del sistema. El pipeline `DataQuery` (consultas SQL/agregaciones) está marcado como futuro (retorna 501 Not Implemented).
