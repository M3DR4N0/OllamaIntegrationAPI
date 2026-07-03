# API Endpoints — Referencia de Uso

> Base URL: `http://localhost:<puerto>/api`

---

## 1. `POST /api/query`

Consulta de texto libre contra la base de conocimiento legal (RAG). El orquestador clasifica la intención automáticamente.

### Request
```http
POST /api/query
Content-Type: application/json
```

```json
{
  "query": "¿Qué dice el artículo 5 sobre aranceles de importación?",
  "model": "mistral",
  "topK": 5
}
```

| Campo   | Tipo     | Requerido | Descripción                                      |
|---------|----------|-----------|--------------------------------------------------|
| `query` | `string` | ✅        | Pregunta o consulta en lenguaje natural           |
| `model` | `string` | ✅        | Nombre del modelo Ollama (ej. `mistral`, `llama3`) |
| `topK`  | `int`    | ❌        | Número de chunks a recuperar del vector store (default: 5) |

### Response
```json
{
  "isSuccess": true,
  "data": {
    "answer": "Según el artículo 5 de la resolución...",
    "context_used": 4,
    "intent": "legal_rag"
  }
}
```

| Campo          | Descripción                                                                 |
|----------------|-----------------------------------------------------------------------------|
| `answer`       | Respuesta generada por el LLM. **Siempre presente.**                        |
| `context_used` | Número de chunks del vector store usados como contexto                      |
| `intent`       | Intención detectada: `legal_rag`, `general`, `data_query` o `contract_analysis` |

### Comportamiento según intención detectada
| Intención detectada | Condición                              | Qué hace                                      |
|---------------------|----------------------------------------|-----------------------------------------------|
| `legal_rag`         | Query contiene palabras legales        | Busca en vector store + responde con ese contexto |
| `general`           | Sin palabras clave específicas         | Intenta RAG, si no hay contexto responde con conocimiento general |
| `data_query`        | Query contiene `total`, `cuántos`, etc.| Retorna `501 Not Implemented` (feature pendiente) |

> **Si el vector store está vacío:** no falla. Cae automáticamente a respuesta general del LLM.

---

## 2. `POST /api/query/with-file`

Igual que `/api/query` pero acepta un archivo adicional como contexto temporal para el análisis.

### Request
```http
POST /api/query/with-file
Content-Type: multipart/form-data
```

| Campo   | Tipo       | Requerido | Descripción                              |
|---------|------------|-----------|------------------------------------------|
| `query` | `string`   | ✅        | Pregunta en lenguaje natural              |
| `model` | `string`   | ✅        | Nombre del modelo Ollama                  |
| `topK`  | `int`      | ❌        | Chunks a recuperar del vector store       |
| `file`  | `IFormFile`| ❌        | Archivo a usar como contexto (PDF, Word, imagen) |

### Response
```json
{
  "isSuccess": true,
  "data": {
    "answer": "El contrato establece en su cláusula 3...",
    "context_used": 5,
    "intent": "contract_analysis"
  }
}
```

> Si se envía un archivo, el orquestador lo clasifica como `contract_analysis` y llama al pipeline de análisis. Si no se envía archivo, el comportamiento es idéntico a `/api/query`.

---

## 3. `POST /api/analysis/contract`

Análisis profundo de un contrato subido por el usuario. El contrato **nunca se persiste** en el vector store — se procesa solo en memoria para esa llamada.

### Request
```http
POST /api/analysis/contract
Content-Type: multipart/form-data
```

| Campo          | Tipo        | Requerido | Descripción                              |
|----------------|-------------|-----------|------------------------------------------|
| `contractFile` | `IFormFile` | ✅        | Archivo del contrato (PDF, Word, imagen)  |
| `query`        | `string`    | ✅        | Pregunta o aspecto a analizar del contrato |
| `model`        | `string`    | ✅        | Nombre del modelo Ollama                  |
| `topK`         | `int`       | ❌        | Chunks legales a recuperar del vector store (default: 5) |

### Response
```json
{
  "isSuccess": true,
  "data": {
    "answer": "El contrato presenta una cláusula de penalización en la sección 4.2 que podría..."
  }
}
```

| Campo    | Descripción                                                   |
|----------|---------------------------------------------------------------|
| `answer` | Análisis generado por el LLM. **Siempre presente.**           |

### Pipeline interno
1. Extrae texto del contrato
2. Divide el contrato en chunks (solo en memoria)
3. Selecciona los chunks más relevantes a la query (cosine similarity)
4. Consulta el vector store en busca de leyes/regulaciones relacionadas *(opcional — no falla si está vacío)*
5. Combina contrato + contexto legal → LLM → `answer`

> **Si el vector store está vacío:** el análisis se realiza igualmente usando solo el texto del contrato.

---

## 4. `POST /api/document/extract-file`

Extracción de información o metadata de un documento con un prompt libre. Usa RAG si hay documentos ingestados.

### Request
```http
POST /api/document/extract-file
Content-Type: multipart/form-data
```

| Campo    | Tipo        | Requerido | Descripción                                     |
|----------|-------------|-----------|------------------------------------------------|
| `file`   | `IFormFile` | ✅        | Documento a procesar (PDF, Word, imagen, TIFF)  |
| `prompt` | `string`    | ✅        | Instrucción o pregunta sobre el documento       |
| `model`  | `string`    | ✅        | Nombre del modelo Ollama                        |

### Response
La respuesta depende del prompt enviado. Para extracción de metadata estructurada, el LLM retorna JSON. Para preguntas libres, retorna texto.

```json
{
  "isSuccess": true,
  "data": {
    "answer": "..."
  }
}
```

> Este es el **único endpoint** donde el LLM puede retornar un JSON estructurado dentro de `answer`, si el prompt lo solicita explícitamente.

---

## 5. `POST /api/document/to-base64`

Convierte cualquier documento recibido por `multipart/form-data` a Base64 sin usar IA.

### Request
```http
POST /api/document/to-base64
Content-Type: multipart/form-data
```

| Campo  | Tipo        | Requerido | DescripciÃ³n                                  |
|--------|-------------|-----------|-----------------------------------------------|
| `file` | `IFormFile` | âœ…        | Documento a convertir (PDF, Word, imagen, etc.) |

### Response
```json
{
  "fileName": "documento1.pdf",
  "base64": "JVBERi0xLjQKJ..."
}
```

| Campo      | DescripciÃ³n                                       |
|------------|---------------------------------------------------|
| `fileName` | Nombre original del archivo incluyendo extensiÃ³n |
| `base64`   | Contenido completo del archivo codificado en Base64 |

---

## Resumen comparativo

| Endpoint                        | Requiere archivo | Persiste en BD vectorial | Consulta BD vectorial | Respuesta estructurada fija |
|---------------------------------|-----------------|--------------------------|----------------------|-----------------------------|
| `POST /api/query`               | ❌              | ❌                       | ✅ (si hay datos)     | `answer`, `context_used`, `intent` |
| `POST /api/query/with-file`     | ❌ (opcional)   | ❌                       | ✅ (si hay datos)     | `answer`, `context_used`, `intent` |
| `POST /api/analysis/contract`   | ✅              | ❌                       | ✅ (si hay datos)     | `answer`                    |
| `POST /api/document/extract-file` | ✅            | ❌                       | ✅ (si hay datos)     | `answer` (puede contener JSON) |

---

## Notas generales

- **Todos los endpoints funcionan sin documentos ingestados.** El vector store vacío nunca provoca un error — simplemente no aporta contexto adicional.
- El modelo por defecto recomendado es `mistral` (Mistral 7B Q4).
- Los archivos soportados incluyen: PDF, Word (`.docx`), imágenes (JPG, PNG) y TIFF.
