# 🚀 Guía de Despliegue — OllamaIntegrationAPI

> Guía paso a paso para montar el sistema desde cero con **Docker Desktop en Windows** (contenedores Linux).

---

## 📋 Tabla de Contenidos

1. [Pre-requisitos](#1-pre-requisitos)
2. [Clonar el repositorio](#2-clonar-el-repositorio)
3. [Crear el archivo .env](#3-crear-el-archivo-env)
4. [Levantar los contenedores](#4-levantar-los-contenedores)
5. [Descargar los modelos de IA](#5-descargar-los-modelos-de-ia)
6. [Verificar que todo funciona](#6-verificar-que-todo-funciona)
7. [Probar cada endpoint](#7-probar-cada-endpoint)
8. [Usar Swagger UI (interfaz visual)](#8-usar-swagger-ui-interfaz-visual)
9. [Comandos útiles del día a día](#9-comandos-útiles-del-día-a-día)
10. [Diagrama del sistema](#10-diagrama-del-sistema)
11. [Checklist final](#11-checklist-final)
12. [Solución de problemas](#12-solución-de-problemas)

---

## 1. Pre-requisitos

| Requisito | Mínimo | Recomendado |
|---|---|---|
| **Docker Desktop** (con WSL2 backend) | ✅ Requerido | — |
| **Git** | ✅ Requerido | — |
| **RAM** | 16 GB | 32 GB |
| **Disco** | 15 GB libres | 30 GB |
| **GPU** | No requerida | NVIDIA (acelera el LLM de 60s → 5s) |

> 🔗 Descarga Docker Desktop: [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop)
>
> Asegúrate de activar **WSL2 backend** durante la instalación.

---

## 2. Clonar el repositorio

```powershell
cd D:\Repository
git clone https://github.com/M3DR4N0/OllamaIntegrationAPI.git
cd OllamaIntegrationAPI
```

---

## 3. Crear el archivo `.env`

El archivo `.env` está en `.gitignore` — **no se sube al repositorio**. Hay que crearlo manualmente en cada máquina:

```powershell
# Solo necesario si usas modelos de HuggingFace (el servicio llama.cpp comentado).
# Si solo usas Ollama, puedes dejarlo así:
echo "HUGGINGFACE_HUB_TOKEN=tu_token_aqui" > .env
```

> ⚠️ **NUNCA** subas tokens reales al repositorio.

---

## 4. Levantar los contenedores

> **Importante:** NO uses `docker compose up` sin `-f`. El archivo `docker-compose.override.yml` usa variables de Visual Studio (`${APPDATA}`) que no existen en una terminal normal y causarán error.

```powershell
# Desde la raíz del repo (donde está docker-compose.yml)
docker compose -f docker-compose.yml up -d --build
```

Esto levanta **3 contenedores**:

| Contenedor | Puerto externo | Función |
|---|---|---|
| `llamaintegrationapi` | `localhost:5000` | Tu API .NET 9 |
| `ollama` | `localhost:11434` | Servidor LLM local |
| `qdrant` | `localhost:6333` (REST) / `localhost:6334` (gRPC) | Base de datos vectorial |

La **primera vez** tarda **5-10 minutos** porque:
- Descarga las imágenes base de Docker (~2 GB)
- Compila el proyecto .NET 9
- Instala Tesseract OCR en el contenedor

### Verificar que los 3 están corriendo

```powershell
docker compose -f docker-compose.yml ps
```

Debes ver los 3 con estado `Up`:

```
NAME                      STATUS
ollama                    Up
qdrant                    Up
llamaintegrationapi       Up
```

---

## 5. Descargar los modelos de IA

Ollama arranca **vacío**. Necesitas descargar 2 modelos (una sola vez — se persisten en el volumen `ollama_models`):

### 5.1 — Modelo de lenguaje

```powershell
# mistral = ~4.1 GB — tarda 5-15 min según tu internet
docker compose -f docker-compose.yml exec ollama ollama pull mistral
```

### 5.2 — Modelo de embeddings

```powershell
# nomic-embed-text = ~274 MB — rápido
docker compose -f docker-compose.yml exec ollama ollama pull nomic-embed-text
```

### 5.3 — Verificar que se descargaron

```powershell
docker compose -f docker-compose.yml exec ollama ollama list
```

Resultado esperado:

```
NAME                     SIZE
mistral:latest           4.1 GB
nomic-embed-text:latest  274 MB
```

> 💡 Los modelos se guardan en el volumen Docker `ollama_models`. **No se pierden** al reiniciar o apagar los contenedores. Solo se borran si ejecutas `docker compose down -v` (el flag `-v` borra volúmenes).

---

## 6. Verificar que todo funciona

### 6.1 — ¿La API arrancó?

```powershell
curl http://localhost:5000/api/inner/ping
```

✅ Respuesta esperada:

```json
{"message":"Pong"}
```

❌ Si no responde, revisa los logs:

```powershell
docker compose -f docker-compose.yml logs llamaintegrationapi --tail 50
```

### 6.2 — ¿Qdrant responde?

Abre en tu navegador:

```
http://localhost:6333/dashboard
```

Verás el dashboard visual de Qdrant. La primera vez estará vacío (aún no has ingestado documentos).

### 6.3 — ¿Ollama responde?

```powershell
curl http://localhost:11434/api/tags
```

✅ Respuesta: JSON con la lista de modelos instalados.

---

## 7. Probar cada endpoint

El flujo completo de tu sistema es:

```
PASO A: Ingestar documentos legales de referencia (llenar Qdrant)
           ↓
PASO B: Analizar / extraer / consultar documentos nuevos (usar RAG contra Qdrant)
```

### 7.1 — Ingestar un documento legal (llenar la base RAG)

Este es el **primer paso obligatorio**. Sin documentos ingestados, el RAG no tiene contexto legal contra el cual comparar.

```powershell
curl -X POST http://localhost:5000/api/ingestion/upload `
  -F "File=@C:\ruta\a\tu\ley_aduanas.pdf" `
  -F "DocumentType=ley" `
  -F "Source=Congreso de la Republica"
```

> ⏱️ **Tarda 30-120 segundos** la primera vez (Ollama carga el modelo de embeddings en memoria).

✅ Respuesta esperada:

```json
{
  "statusCode": 200,
  "success": true,
  "data": { "chunksIngested": 45 }
}
```

**¿Qué pasó internamente?**

```
Tu PDF
  → Tesseract OCR (si es escaneado)
  → Extracción de texto
  → Dividido en 45 chunks semánticos (artículos, secciones, cláusulas)
  → Cada chunk → embedding de 768 dimensiones (nomic-embed-text)
  → Almacenado en Qdrant (colección "legal_documents")
```

> 💡 Puedes verificar en `http://localhost:6333/dashboard` que se creó la colección `legal_documents` con puntos.

Repite este paso con todos los documentos legales/regulatorios que quieras usar como referencia.

---

### 7.2 — Consulta RAG (preguntar sobre lo ingestado)

```powershell
curl -X POST http://localhost:5000/api/query `
  -H "Content-Type: application/json" `
  -d "{\"query\": \"Cuales son los requisitos para importar alimentos?\", \"model\": \"mistral\", \"topK\": 5}"
```

> ⏱️ **Tarda 10-60 segundos** según tu hardware (sin GPU tarda más).

✅ Respuesta esperada:

```json
{
  "statusCode": 200,
  "success": true,
  "data": {
    "answer": "Según el Artículo 15 de la Ley de Aduanas...",
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

**¿Qué pasó internamente?**

```
Tu pregunta
  → Embedding de la pregunta (nomic-embed-text)
  → Búsqueda de similitud coseno en Qdrant (top 5 chunks más relevantes)
  → Prompt enriquecido: pregunta + chunks legales encontrados
  → mistral genera respuesta fundamentada en el contexto
  → Respuesta + fuentes citadas
```

---

### 7.3 — Analizar un contrato contra la base legal

```powershell
curl -X POST http://localhost:5000/api/analysis/contract `
  -F "ContractFile=@C:\ruta\a\tu\contrato.pdf" `
  -F "Query=Verifica cumplimiento con normativa aduanera" `
  -F "Model=mistral" `
  -F "TopK=10"
```

> ⏱️ **Tarda 30-120 segundos** (parsea el contrato + busca contexto legal + genera análisis).

✅ Respuesta esperada:

```json
{
  "statusCode": 200,
  "success": true,
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

**¿Qué pasó internamente?**

```
Tu contrato PDF
  → OCR / extracción de texto
  → Dividido en chunks (en memoria, NO se almacena en Qdrant)
  → Ranking: selecciona los 12 chunks más relevantes para tu pregunta
  → Busca contexto legal en Qdrant (top 10 chunks legales relevantes)
  → Prompt: chunks del contrato + chunks legales + pregunta
  → mistral genera análisis estructurado en JSON
  → Fallback: si el JSON falla, reintenta como texto libre
```

---

### 7.4 — Extraer metadata de un documento (endpoint para Laserfiche)

```powershell
curl -X POST http://localhost:5000/api/document/extract-file `
  -F "File=@C:\ruta\a\tu\factura.pdf" `
  -F "Prompt=Extrae numero de factura, fecha, monto total y productos en formato JSON" `
  -F "Model=mistral" `
  -F "Format=json"
```

✅ Respuesta: JSON con la metadata extraída por el LLM.

**¿Qué pasó internamente?**

```
Tu factura/documento
  → OCR / extracción de texto
  → Chunking semántico
  → Ranking: top 10 chunks más relevantes para tu prompt
  → Contexto legal de Qdrant (si aplica)
  → Prompt enriquecido → mistral → JSON con metadata
```

> 💡 Este es el endpoint que conectas a **Laserfiche**. El flujo del script sería:
> 1. Laserfiche envía el documento via HTTP POST multipart
> 2. La API procesa y devuelve JSON con la metadata
> 3. Laserfiche usa el JSON para llenar campos del documento

---

### 7.5 — Consulta con archivo adjunto (orquestación automática)

```powershell
curl -X POST http://localhost:5000/api/query/with-file `
  -F "Query=Analiza este contrato" `
  -F "Model=mistral" `
  -F "TopK=5" `
  -F "file=@C:\ruta\a\tu\contrato.pdf"
```

El orquestador detecta automáticamente que hay un archivo adjunto y lo enruta al pipeline de análisis de contratos.

---

## 8. Usar Swagger UI (interfaz visual)

Swagger UI te permite probar todos los endpoints desde el navegador **sin usar `curl`**. Útil para pruebas rápidas.

### Opción A — Usar `docker-compose.dev.yml`

```powershell
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```

Luego abre en tu navegador:

```
http://localhost:5000/swagger
```

### Opción B — Variable de entorno directa

```powershell
docker compose -f docker-compose.yml up -d --build
docker compose -f docker-compose.yml exec -e ASPNETCORE_ENVIRONMENT=Development llamaintegrationapi dotnet LlamaIntegrationAPI.dll
```

> ⚠️ **NO uses `docker compose up` sin `-f`**. El `docker-compose.override.yml` es exclusivo de Visual Studio y usa `${APPDATA}` que no existe en PowerShell.

---

## 9. Comandos útiles del día a día

### Gestión de contenedores

```powershell
# Ver estado de los contenedores
docker compose -f docker-compose.yml ps

# Ver logs en tiempo real de la API
docker compose -f docker-compose.yml logs -f llamaintegrationapi

# Ver logs de todos los servicios
docker compose -f docker-compose.yml logs -f

# Reiniciar solo la API (después de cambios en el código)
docker compose -f docker-compose.yml up -d --build llamaintegrationapi

# Parar todo
docker compose -f docker-compose.yml down

# Parar todo Y BORRAR los datos (Qdrant + modelos Ollama)
docker compose -f docker-compose.yml down -v
```

### Gestión de modelos

```powershell
# Listar modelos instalados
docker compose -f docker-compose.yml exec ollama ollama list

# Descargar un modelo nuevo
docker compose -f docker-compose.yml exec ollama ollama pull <nombre_modelo>

# Borrar un modelo
docker compose -f docker-compose.yml exec ollama ollama rm <nombre_modelo>
```

### Depuración

```powershell
# Entrar al contenedor de la API
docker compose -f docker-compose.yml exec llamaintegrationapi bash

# Ver cuánto espacio usan los contenedores
docker system df

# Limpiar imágenes/contenedores no usados
docker system prune
```

---

## 10. Diagrama del sistema

```
TU MÁQUINA WINDOWS (Docker Desktop + WSL2)
│
├── docker compose -f docker-compose.yml up -d --build
│
│   ┌─────────────────────────────────────────────────────────────┐
│   │                      RED INTERNA DOCKER                     │
│   │                                                             │
│   │  ┌────────────────────┐  ┌────────────┐  ┌──────────────┐  │
│   │  │  llamaintegrationapi │  │   ollama    │  │    qdrant    │  │
│   │  │  (.NET 9 API)      │  │   (LLM)    │  │  (vectorDB)  │  │
│   │  │  :8080 interno     │─►│  :11434    │  │  :6334 gRPC  │  │
│   │  │                    │  │            │  │              │  │
│   │  │  • Tesseract OCR   │  │  • mistral │  │  • legal_    │  │
│   │  │  • ChunkingService │─►│  • nomic-  │  │    documents │  │
│   │  │  • EmbeddingService│  │    embed   │  │              │  │
│   │  │  • AnalysisService │  │            │  │  Dashboard:  │  │
│   │  │  • OrchestratorSvc │  │            │  │  :6333/dash  │  │
│   │  └────────────────────┘  └────────────┘  └──────────────┘  │
│   │          ▲                                                  │
│   └──────────│──────────────────────────────────────────────────┘
│              │
│              │ localhost:5000
│              │
│   ┌──────────┴───────────────────────┐
│   │  Laserfiche Script / curl /      │
│   │  Swagger UI / tu aplicación      │
│   └──────────────────────────────────┘
```

### Flujos de datos

```
INGESTA (una vez por documento legal):
  Archivo ──► DocumentParser ──► ChunkingService ──► EmbeddingService ──► Qdrant
                (OCR)           (semántico)         (nomic-embed)       (persistido)

CONSULTA RAG:
  Pregunta ──► EmbeddingService ──► Qdrant Search ──► ContextBuilder ──► LLM ──► Respuesta
               (prompt armado)   (mistral) (con fuentes)

ANÁLISIS DE CONTRATO:
  Contrato ──► Parser ──► Chunker ──► Ranking ──► Qdrant Search ──► LLM ──► AnalysisResult
               (OCR)     (memoria)   (top-12)    (contexto legal)  (JSON)   {compliance,risks}

EXTRACCIÓN (Laserfiche):
  Documento ──► Parser ──► Chunker ──► Ranking ──► Qdrant ──► LLM ──► JSON metadata
                (legal)    (mistral) (lo que pidas)
```

---

## 11. Checklist final

| # | Verificación | Comando | ✅ Esperado |
|---|---|---|---|
| 1 | Docker levantado | `docker compose -f docker-compose.yml ps` | 3 contenedores con estado `Up` |
| 2 | API responde | `curl http://localhost:5000/api/inner/ping` | `{"message":"Pong"}` |
| 3 | Ollama tiene modelos | `docker compose -f docker-compose.yml exec ollama ollama list` | `mistral` + `nomic-embed-text` |
| 4 | Qdrant responde | Navegar a `http://localhost:6333/dashboard` | Dashboard visible |
| 5 | Ingesta funciona | `POST /api/ingestion/upload` con un PDF | `{"data":{"chunksIngested": N}}` |
| 6 | Consulta RAG funciona | `POST /api/query` con una pregunta | Respuesta con `answer` y `sources` |
| 7 | Análisis funciona | `POST /api/analysis/contract` con un PDF | JSON con `compliance`, `risks` |
| 8 | Extracción funciona | `POST /api/document/extract-file` con un PDF | Metadata en formato solicitado |

---

## 12. Solución de problemas

### La API no arranca / error en logs

```powershell
docker compose -f docker-compose.yml logs llamaintegrationapi --tail 100
```

| Error | Causa | Solución |
|---|---|---|
| `Connection refused :11434` | Ollama no ha terminado de arrancar | Espera 30 segundos y reintenta |
| `Connection refused :6334` | Qdrant no ha terminado de arrancar | Espera 30 segundos y reintenta |
| `libtesseract50.so not found` | Tesseract no se instaló bien | Rebuild: `docker compose -f docker-compose.yml build --no-cache llamaintegrationapi` |

### El LLM tarda demasiado (>2 minutos por request)

Sin GPU, `mistral` usa solo CPU. Esto es normal:

| Hardware | Tiempo típico por request |
|---|---|
| Solo CPU (16 GB RAM) | 30-90 segundos |
| NVIDIA GPU (8+ GB VRAM) | 3-10 segundos |

Para usar GPU NVIDIA, agrega esto al servicio `ollama` en `docker-compose.yml`:

```yaml
  ollama:
    image: ollama/ollama:latest
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
```

> Requiere: NVIDIA drivers + [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) instalado.

### `docker compose up` falla con error de `${APPDATA}`

Estás usando `docker compose up` sin `-f`, lo que carga el `docker-compose.override.yml` automáticamente. Ese archivo es solo para Visual Studio.

**Solución:** Siempre usa el flag `-f`:

```powershell
docker compose -f docker-compose.yml up -d --build
```

### La ingesta no encuentra texto en el PDF

El PDF puede ser escaneado (imagen, no texto). Tesseract hace OCR automáticamente, pero:
- Solo tiene datos de **español** (`tessdata/spa.traineddata`)
- Si el documento está en otro idioma, el OCR puede fallar

### Qdrant perdió los datos

Solo pasa si ejecutaste `docker compose down -v` (el flag `-v` borra volúmenes). Sin `-v`, los datos persisten.

```powershell
# ✅ Seguro — los datos persisten
docker compose -f docker-compose.yml down

# ⚠️ BORRA TODO — modelos de Ollama + datos de Qdrant
docker compose -f docker-compose.yml down -v
```
