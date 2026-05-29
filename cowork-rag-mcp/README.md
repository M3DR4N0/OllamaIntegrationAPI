# cowork-rag-mcp

MVP tecnico de servidor MCP local para conectar Claude Desktop / Claude Cowork con una API local de RAG documental en .NET 9.

La arquitectura objetivo es:

```text
Claude Desktop / Claude Cowork
  -> MCP Server local Node.js/TypeScript
    -> API .NET local http://localhost:5000
      -> Ollama + Qdrant + OCR
```

El modelo por defecto es `gemma3:1b`.

## Objetivo

Este MVP permite que Claude use herramientas externas para:

- verificar si la API local esta activa
- consultar la base documental local
- generar contexto para informes ejecutivos
- analizar contratos ya indexados
- enviar un archivo local a la API usando `multipart/form-data`
- recibir respuestas JSON con fuentes, riesgos, obligaciones y resumen si la API los devuelve

## Requisitos

- Node.js 18 o superior
- pnpm
- API .NET local disponible en `http://localhost:5000`

## Instalacion

```powershell
cd C:\apis\OllamaIntegrationAPI\cowork-rag-mcp
pnpm install
```

## Ejecucion local

Modo desarrollo:

```powershell
pnpm dev
```

Compilar:

```powershell
pnpm build
```

Ejecutar compilado:

```powershell
pnpm start
```

Importante: el servidor usa transporte `stdio`. No imprime logs normales por `stdout`, porque eso rompe el protocolo MCP. Los errores se envian por `stderr`.

## Variables de entorno

```powershell
RAG_API_URL=http://localhost:5000
RAG_DEFAULT_MODEL=gemma3:1b
RAG_TIMEOUT_MS=60000
RAG_ALLOWED_FILE_ROOT=C:\Temp
```

`RAG_ALLOWED_FILE_ROOT` es opcional. Si se configura, la herramienta `analizar_archivo_local` solo permite leer archivos dentro de esa carpeta.

## Configuracion en Claude Desktop para Windows

Archivo aproximado:

```text
%APPDATA%\Claude\claude_desktop_config.json
```

Ejemplo:

```json
{
  "mcpServers": {
    "fp-local-rag": {
      "command": "pnpm",
      "args": [
        "--dir",
        "C:\\apis\\OllamaIntegrationAPI\\cowork-rag-mcp",
        "dev"
      ],
      "env": {
        "RAG_API_URL": "http://localhost:5000",
        "RAG_DEFAULT_MODEL": "gemma3:1b",
        "RAG_TIMEOUT_MS": "60000",
        "RAG_ALLOWED_FILE_ROOT": "C:\\Temp"
      }
    }
  }
}
```

Despues de editar el archivo, reinicia Claude Desktop.

## Herramientas disponibles

### health_check_rag

Verifica si la API local esta disponible.

Primero intenta:

```http
GET /health
```

Si falla, intenta:

```http
GET /
```

Respuesta esperada cuando la API responde:

```json
{
  "success": true,
  "message": "API local disponible",
  "url": "http://localhost:5000"
}
```

### consultar_base_documental

Consulta la base documental local.

Endpoint:

```http
POST /api/query
```

Parametros:

- `pregunta`: string requerido
- `topK`: number opcional, default `5`
- `model`: string opcional, default `gemma3:1b`

Payload enviado a la API:

```json
{
  "query": "pregunta del usuario",
  "model": "gemma3:1b",
  "topK": 5
}
```

### generar_informe_contextual

Construye una pregunta estructurada para preparar contexto de informe ejecutivo y llama a:

```http
POST /api/query
```

Parametros:

- `tema`: string requerido
- `incluirRiesgos`: boolean opcional, default `true`
- `incluirObligaciones`: boolean opcional, default `true`
- `incluirFechasCriticas`: boolean opcional, default `true`
- `incluirFuentes`: boolean opcional, default `true`
- `topK`: number opcional, default `10`
- `model`: string opcional, default `gemma3:1b`

### analizar_contrato_local

Analiza contratos o documentos legales ya indexados.

Endpoint:

```http
POST /api/analysis/contract
```

Payload:

```json
{
  "query": "pregunta legal o contractual",
  "model": "gemma3:1b",
  "topK": 8
}
```

### listar_documentos_locales

Lista documentos indexados o disponibles.

Endpoint:

```http
GET /api/agent/documents
```

Si la API devuelve `404`, el MCP responde:

```text
El endpoint /api/agent/documents todavia no esta implementado en la API .NET.
```

### analizar_archivo_local

Envia un archivo local a la API con `multipart/form-data`.

Endpoint:

```http
POST /api/analysis/contract
```

Parametros:

- `filePath`: string requerido
- `pregunta`: string requerido
- `model`: string opcional, default `gemma3:1b`
- `topK`: number opcional, default `8`

Campos enviados:

```text
file = archivo leido desde filePath
query = pregunta
model = model
topK = topK
```

El campo del archivo debe llamarse exactamente `file`. No uses nombres alternativos para el campo multipart del archivo.

Extensiones esperadas:

- `.pdf`
- `.docx`
- `.txt`
- `.tif`
- `.tiff`
- `.png`
- `.jpg`
- `.jpeg`

Si se envia `.tif` o `.tiff`, se asume que puede ser TIFF multipagina. La API local debe encargarse de procesar todas las paginas.

## Prompts de prueba

```text
Usa health_check_rag para verificar si mi API local esta disponible.
```

```text
Usa consultar_base_documental para buscar obligaciones, riesgos y fechas criticas relacionadas con los documentos cargados.
```

```text
Usa generar_informe_contextual para preparar un informe ejecutivo sobre los contratos cargados. Incluye resumen, riesgos, obligaciones, fechas criticas, fuentes y preguntas pendientes.
```

```text
Usa analizar_archivo_local con este archivo: C:\Temp\contrato.pdf. Analiza obligaciones, riesgos, fechas criticas y recomendaciones.
```

```text
Usa analizar_archivo_local con este archivo: C:\Temp\documento-multipagina.tif. Extrae y analiza el contenido completo del documento.
```

## Troubleshooting

Si Claude no detecta el MCP:

- confirma que `pnpm install` se ejecuto correctamente
- valida que la ruta en `--dir` exista
- reinicia Claude Desktop despues de modificar `claude_desktop_config.json`
- ejecuta `pnpm build` para detectar errores TypeScript

Si `health_check_rag` falla:

- confirma que la API .NET esta corriendo en `http://localhost:5000`
- revisa si la API expone `GET /health` o al menos `GET /`
- verifica firewall, puertos y logs de la API

Si una respuesta no se puede parsear como JSON:

- el MCP devuelve el texto crudo
- revisa el endpoint de la API para confirmar si esta devolviendo `application/json`

Si `analizar_archivo_local` falla:

- confirma que el archivo exista
- confirma que no sea un directorio
- si usas `RAG_ALLOWED_FILE_ROOT`, confirma que el archivo este dentro de esa carpeta
- para archivos grandes, revisa limites de request body en la API .NET

## Seguridad

Este MCP no incluye herramientas para borrar, modificar, mover archivos, ejecutar comandos ni ejecutar SQL.

Recomendaciones para produccion:

- proteger la API con API key o token
- limitar `RAG_ALLOWED_FILE_ROOT` a una carpeta controlada
- no exponer variables sensibles en prompts o respuestas
- ejecutar el MCP y la API local con permisos minimos
- validar tamano maximo de archivos en la API .NET

## Notas de compatibilidad

- Usa `fetch`, `FormData` y `Blob` nativos de Node 18+
- Usa transporte MCP por `stdio`
- Usa `@modelcontextprotocol/sdk` y `zod`
- El modelo por defecto es `gemma3:1b`
- Todos los endpoints de archivo unico usan el campo multipart exacto `file`
