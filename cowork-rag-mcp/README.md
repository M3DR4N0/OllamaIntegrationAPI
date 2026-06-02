# cowork-rag-mcp

Servidor MCP local para conectar Claude Desktop con la API RAG documental en .NET 9.

```text
Claude Desktop
  -> MCP Server (Node.js / TypeScript)
    -> API .NET  http://localhost:5000
      -> Ollama + Qdrant + OCR (Ghostscript + Tesseract)
```

---

## Requisitos

- Node.js 18 o superior (`node --version`)
- pnpm (`npm i -g pnpm`)
- API .NET local corriendo en `http://localhost:5000`
- Ollama corriendo con los modelos `gemma3:1b` y `nomic-embed-text`

---

## Instalacion y compilacion

```powershell
cd D:\Repository\MANDEZCA\LlamaIntegrationAPI\cowork-rag-mcp
pnpm install
pnpm build
```

El output compilado queda en `dist/`.

---

## Configuracion en Claude Desktop

Archivo de configuracion:

```
%APPDATA%\Claude\claude_desktop_config.json
```

**Usa siempre `node` con ruta absoluta - NO uses `pnpm dev` porque Claude Desktop puede no tener pnpm en el PATH.**

```json
{
  "mcpServers": {
    "fp-local-rag": {
      "command": "C:\\Program Files\\nodejs\\node.exe",
      "args": [
        "D:\\Repository\\MANDEZCA\\LlamaIntegrationAPI\\cowork-rag-mcp\\dist\\server.js"
      ],
      "env": {
        "RAG_API_URL": "http://localhost:5000",
        "RAG_DEFAULT_MODEL": "gemma3:1b",
        "RAG_TIMEOUT_MS": "120000",
        "RAG_ALLOWED_FILE_ROOT": "C:\\Temp"
      }
    }
  }
}
```

> **Importante:** despues de editar el archivo, cierra Claude Desktop completamente desde la bandeja del sistema y vuelve a abrirlo.

---

## Variables de entorno

| Variable | Default | Descripcion |
|---|---|---|
| `RAG_API_URL` | `http://localhost:5000` | URL base de la API .NET |
| `RAG_DEFAULT_MODEL` | `gemma3:1b` | Modelo Ollama a usar |
| `RAG_TIMEOUT_MS` | `120000` | Timeout por peticion en ms |
| `RAG_ALLOWED_FILE_ROOT` | (ninguno) | Restringe `analizar_archivo_local` a esa carpeta |

> El timeout es 120 segundos porque el analisis de PDFs escaneados (OCR + embeddings + LLM) puede tardar entre 1 y 3 minutos.

---

## Herramientas disponibles

### `health_check_rag`
Verifica si la API local esta disponible. Intenta `GET /health` y si falla intenta `GET /`.

Sin parametros.

---

### `consultar_base_documental`
Consulta la base documental mediante `POST /api/query`.

| Parametro | Tipo | Default | Descripcion |
|---|---|---|---|
| `pregunta` | string | requerido | Pregunta en lenguaje natural |
| `topK` | number | 5 | Chunks a recuperar del vector store |
| `model` | string | gemma3:1b | Modelo Ollama |

Respuesta esperada:
```json
{ "data": { "answer": "...", "context_used": 4, "intent": "legal_rag" } }
```

---

### `generar_informe_contextual`
Construye una query estructurada para informe ejecutivo y llama a `POST /api/query`.

| Parametro | Tipo | Default |
|---|---|---|
| `tema` | string | requerido |
| `incluirRiesgos` | boolean | true |
| `incluirObligaciones` | boolean | true |
| `incluirFechasCriticas` | boolean | true |
| `incluirFuentes` | boolean | true |
| `topK` | number | 10 |
| `model` | string | gemma3:1b |

---

### `analizar_contrato_local`
Analiza un contrato por texto (sin subir archivo) mediante `POST /api/query`.

| Parametro | Tipo | Default |
|---|---|---|
| `pregunta` | string | requerido |
| `topK` | number | 8 |
| `model` | string | gemma3:1b |

---

### `listar_documentos_locales`
Lista documentos indexados en el vector store mediante `GET /api/agent/documents`.

Sin parametros. Devuelve 404 manejado si el endpoint no esta implementado.

---

### `analizar_archivo_local`
Sube un archivo local a `POST /api/analysis/contract` via `multipart/form-data`. Soporta PDF, Word, imagenes y TIFF.

| Parametro | Tipo | Default |
|---|---|---|
| `filePath` | string | requerido - ruta absoluta al archivo |
| `pregunta` | string | requerido |
| `model` | string | gemma3:1b |
| `topK` | number | 8 |

Respuesta esperada:
```json
{ "data": { "answer": "El contrato establece en su clausula 3..." } }
```

---

## Diagnostico de problemas comunes

| Sintoma | Causa probable | Solucion |
|---|---|---|
| Claude Desktop no muestra las herramientas | `pnpm` no esta en el PATH de Claude | Usa `node.exe` con ruta absoluta en el config |
| `Error: Cannot find module` | No se compilo el proyecto | Ejecuta `pnpm build` |
| `ECONNREFUSED` en todas las herramientas | La API .NET no esta corriendo | Levanta Docker Compose |
| Timeout en `analizar_archivo_local` | PDF grande con OCR | Aumenta `RAG_TIMEOUT_MS` a 180000 |
| Respuesta en ingles | El modelo ignoro la instruccion de idioma | El system prompt ya fuerza espanol - prueba con modelo `gemma3:1b` |
