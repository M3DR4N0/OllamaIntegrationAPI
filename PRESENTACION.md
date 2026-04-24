# 📘 OllamaIntegrationAPI — Documentación del Proyecto

> **Sistema de Inteligencia Artificial** para análisis de documentos legales, consultas inteligentes y revisión de contratos.

---

## 📑 Índice

1. [¿Qué es este proyecto?](#1-qué-es-este-proyecto)
2. [¿Qué problema resuelve?](#2-qué-problema-resuelve)
3. [¿Cómo funciona?](#3-cómo-funciona)
4. [Componentes del sistema](#4-componentes-del-sistema)
5. [¿Qué puede hacer la API?](#5-qué-puede-hacer-la-api)
6. [Flujos de trabajo](#6-flujos-de-trabajo)
7. [Tecnologías utilizadas](#7-tecnologías-utilizadas)
8. [Estructura del proyecto](#8-estructura-del-proyecto)
9. [Requisitos para ejecutar](#9-requisitos-para-ejecutar)
10. [Ejemplos de uso](#10-ejemplos-de-uso)
11. [Integración con Laserfiche](#11-integración-con-laserfiche)
12. [Preguntas frecuentes](#12-preguntas-frecuentes)

---

## 1. ¿Qué es este proyecto?

**OllamaIntegrationAPI** es una plataforma web que utiliza **Inteligencia Artificial** para leer, entender y analizar documentos legales de forma automática.

Funciona como un **asistente legal digital** que puede:

- 📄 **Leer documentos** — Extrae texto de PDFs, documentos Word e imágenes (incluso documentos escaneados).
- 🧠 **Aprender de leyes y regulaciones** — Almacena el contenido de documentos legales para usarlos como referencia.
- 💬 **Responder preguntas** — Consulta su base de conocimiento legal para dar respuestas fundamentadas con citas de artículos y secciones.
- ⚖️ **Analizar contratos** — Compara un contrato contra la normativa legal almacenada e identifica riesgos o incumplimientos.
- 🏷️ **Extraer información** — Obtiene datos específicos de documentos (números de factura, fechas, montos, etc.) en formato estructurado.

---

## 2. ¿Qué problema resuelve?

### Sin este sistema:

| Tarea | Cómo se hace hoy | Tiempo |
|---|---|---|
| Revisar si un contrato cumple con la ley | Un abogado lee el contrato y cruza manualmente con la normativa | Horas o días |
| Extraer datos de una factura | Alguien los copia a mano al sistema | 5-15 minutos por documento |
| Buscar un artículo legal específico | Buscar manualmente en múltiples documentos | Variable |
| Verificar cumplimiento regulatorio | Consultar a expertos legales | Depende de disponibilidad |

### Con este sistema:

| Tarea | Cómo se hace | Tiempo |
|---|---|---|
| Revisar si un contrato cumple con la ley | Se sube el contrato y la IA lo analiza automáticamente | 30-120 segundos |
| Extraer datos de una factura | Se sube el documento y la IA extrae los datos en JSON | 10-30 segundos |
| Buscar un artículo legal específico | Se hace una pregunta en lenguaje natural | 10-60 segundos |
| Verificar cumplimiento regulatorio | La IA cruza el documento con toda la normativa almacenada | 30-120 segundos |

---

## 3. ¿Cómo funciona?

El sistema utiliza una técnica llamada **RAG** (Generación Aumentada por Recuperación). En términos simples:

### Paso 1 — Alimentar el sistema (una sola vez por documento)

Se suben las leyes y regulaciones. El sistema:

1. **Lee** el documento (si está escaneado, usa reconocimiento óptico de caracteres)
2. **Divide** el texto en fragmentos organizados (artículos, secciones, cláusulas)
3. **Convierte** cada fragmento en una representación numérica que la IA puede comparar
4. **Almacena** todo en una base de datos especializada para búsquedas rápidas

### Paso 2 — Consultar o analizar (las veces que quiera)

Cuando se hace una pregunta o se sube un documento para analizar:

1. La IA **busca** los fragmentos legales más relevantes para la consulta
2. **Combina** esos fragmentos con la pregunta del usuario
3. El modelo de IA **genera** una respuesta basada específicamente en la normativa encontrada
4. Devuelve la respuesta **con citas** de los artículos y secciones utilizados

### ¿Por qué es mejor que solo preguntarle a una IA genérica?

| IA genérica (ChatGPT, etc.) | Este sistema |
|---|---|
| Puede inventar información | Solo responde con base en documentos reales que se le proporcionaron |
| No conoce la normativa específica de tu país/industria | Trabaja con las leyes y regulaciones que tú le das |
| No cita fuentes verificables | Cita artículos y secciones específicas |
| Requiere acceso a internet | Funciona 100% local — tus documentos nunca salen de tu red |

---

## 4. Componentes del sistema

El sistema se compone de **3 partes principales** que trabajan juntas:

```
┌─────────────────────────────────────────────────────────┐
│                    TU COMPUTADORA                       │
│                                                         │
│  ┌─────────────────┐  ┌────────────┐  ┌──────────────┐ │
│  │                  │  │            │  │              │ │
│  │   🌐 LA API     │  │  🧠 LA IA  │  │  📚 LA BASE  │ │
│  │   (.NET 9)      │  │  (Ollama)  │  │  (Qdrant)    │ │
│  │                  │  │            │  │              │ │
│  │  Recibe los     │  │ Entiende   │  │ Almacena y   │ │
│  │  documentos,    │──│ el texto   │  │ busca los    │ │
│  │  los procesa y  │  │ y genera   │  │ fragmentos   │ │
│  │  coordina todo  │──│ respuestas │  │ legales      │ │
│  │                  │  │            │  │              │ │
│  └─────────────────┘  └────────────┘  └──────────────┘ │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### 🌐 La API (OllamaIntegrationAPI)

Es el **cerebro coordinador**. Recibe las solicitudes del usuario, procesa los documentos, y organiza el trabajo entre los otros componentes.

**Responsabilidades:**
- Leer documentos (PDF, Word, imágenes, documentos escaneados)
- Dividir documentos en fragmentos inteligentes
- Coordinar las búsquedas y las respuestas de la IA
- Exponer los servicios a través de internet (HTTP)

### 🧠 La IA (Ollama + Mistral 7B)

Es el **motor de inteligencia artificial**. Se ejecuta localmente en tu máquina (no envía datos a la nube). Tiene dos funciones:

1. **Entender textos** — Convierte texto en representaciones numéricas para poder comparar documentos
2. **Generar respuestas** — Lee el contexto legal proporcionado y genera respuestas inteligentes

**Modelos utilizados:**

| Modelo | Función | Tamaño |
|---|---|---|
| **Mistral 7B** | Genera respuestas, analiza contratos, extrae información | ~4.1 GB |
| **nomic-embed-text** | Convierte texto en representaciones numéricas para búsquedas | ~274 MB |

### 📚 La Base de Datos (Qdrant)

Es la **memoria del sistema**. Almacena todos los fragmentos de leyes y regulaciones de forma que se puedan buscar por similitud de significado (no solo por palabras exactas).

**Ejemplo:** Si preguntas "¿qué se necesita para traer mercancía del extranjero?", el sistema encuentra artículos sobre "importación de bienes" aunque no uses esas palabras exactas.

---

## 5. ¿Qué puede hacer la API?

### 📥 Subir documentos legales (Ingesta)

**Dirección:** `POST /api/ingestion/upload`

Sube una ley, regulación o cualquier documento legal al sistema para que la IA lo use como referencia en futuras consultas.

**Lo que necesita:**
- El archivo (PDF, Word o imagen)
- Qué tipo de documento es (ej: "ley", "regulación", "decreto")
- De dónde viene (ej: "Congreso de la República", "SUNAT")

**Lo que devuelve:**
- Cuántos fragmentos se almacenaron

---

### 💬 Hacer preguntas (Consulta RAG)

**Dirección:** `POST /api/query`

Haz una pregunta en lenguaje natural y el sistema busca en su base de conocimiento legal para responder con fundamento.

**Lo que necesita:**
- La pregunta (en español o inglés)

**Lo que devuelve:**
- La respuesta de la IA
- Las fuentes utilizadas (documento, sección, artículo)

---

### ⚖️ Analizar un contrato

**Dirección:** `POST /api/analysis/contract`

Sube un contrato y el sistema lo compara automáticamente contra toda la normativa legal almacenada.

**Lo que necesita:**
- El archivo del contrato
- Qué deseas verificar (ej: "Verifica cumplimiento con normativa aduanera")

**Lo que devuelve:**
- ✅ o ❌ Si cumple o no con la normativa
- Lista de **riesgos** encontrados
- **Artículos relacionados** de la ley que aplican
- Un **resumen** del análisis

---

### 🏷️ Extraer información de documentos

**Dirección:** `POST /api/document/extract-file`

Sube cualquier documento y pide que la IA extraiga información específica.

**Lo que necesita:**
- El archivo (factura, contrato, certificado, etc.)
- Qué información quieres (ej: "Extrae número de factura, fecha y monto total")

**Lo que devuelve:**
- Los datos solicitados en formato estructurado (JSON)

---

### 💬 Consulta inteligente con archivo

**Dirección:** `POST /api/query/with-file`

Combina una pregunta con un archivo adjunto. El sistema detecta automáticamente qué tipo de análisis hacer.

---

### 🏥 Verificar estado del sistema

| Dirección | Qué hace |
|---|---|
| `GET /api/inner/ping` | Verifica que la API está funcionando |
| `GET /api/inner/health` | Estado de salud del sistema |
| `GET /api/inner/version` | Versión de la API instalada |

---

## 6. Flujos de trabajo

### Flujo 1 — Alimentar el sistema con legislación

```
     📄 Documento legal (PDF)
              │
              ▼
     ┌──────────────────┐
     │  Lectura del      │  El sistema lee el PDF
     │  documento (OCR)  │  (si es escaneado, usa reconocimiento óptico)
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  División en      │  Identifica artículos, secciones y cláusulas
     │  fragmentos       │  automáticamente
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  Conversión a     │  Cada fragmento se convierte en una
     │  representación   │  representación numérica comparable
     │  numérica         │
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  Almacenamiento   │  Se guarda en la base de datos
     │  en Qdrant        │  para búsquedas futuras
     └──────────────────┘
              ▼
     ✅ "45 fragmentos almacenados"
```

### Flujo 2 — Consultar la base legal

```
     💬 "¿Cuáles son los requisitos para importar alimentos?"
              │
              ▼
     ┌──────────────────┐
     │  Búsqueda por     │  Encuentra los artículos más
     │  significado      │  relevantes para la pregunta
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  Armado del       │  Combina los artículos encontrados
     │  contexto         │  con la pregunta del usuario
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  Generación de    │  La IA genera una respuesta
     │  respuesta        │  citando artículos específicos
     └──────────────────┘
              ▼
     📋 "Según el Artículo 15 de la Ley de Aduanas..."
        Fuentes: ley_aduanas.pdf, Capítulo III, Art. 15
```

### Flujo 3 — Analizar un contrato

```
     📄 Contrato + ❓ "¿Cumple con la normativa aduanera?"
              │
              ▼
     ┌──────────────────┐
     │  Lectura del      │  Extrae el texto del contrato
     │  contrato         │
     └────────┬─────────┘
              ▼
     ┌──────────────────┐
     │  División y       │  Divide el contrato en fragmentos
     │  selección        │  y selecciona los más relevantes
     └────────┬─────────┘
              ▼
     ┌──────────────────────────────────────────────┐
     │  Búsqueda de normativa aplicable             │
     │  (encuentra leyes relacionadas en la base)   │
     └────────┬─────────────────────────────────────┘
              ▼
     ┌──────────────────┐
     │  Análisis por     │  La IA compara el contrato
     │  la IA            │  contra la normativa encontrada
     └──────────────────┘
              ▼
     📋 Resultado:
        ❌ No cumple
        ⚠️ Riesgos: "Cláusula 5 no especifica penalidades..."
        📖 Artículos: "Art. 23 - Ley de Comercio Exterior"
        📝 Resumen: "El contrato presenta 2 riesgos principales..."
```

---

## 7. Tecnologías utilizadas

| Tecnología | Qué es | Para qué se usa |
|---|---|---|
| **.NET 9** | Plataforma de desarrollo de Microsoft | Base de la API web |
| **C# 13** | Lenguaje de programación | Código del sistema |
| **ASP.NET Core** | Framework web | Exposición de servicios HTTP |
| **Docker** | Sistema de contenedores | Empaqueta y ejecuta los 3 componentes |
| **Ollama** | Servidor de IA local | Ejecuta los modelos de IA sin internet |
| **Mistral 7B** | Modelo de IA generativa | Genera respuestas, analiza textos |
| **nomic-embed-text** | Modelo de representación numérica | Convierte texto en vectores comparables |
| **Qdrant** | Base de datos vectorial | Almacena y busca fragmentos por significado |
| **Tesseract 5** | Motor de reconocimiento óptico (OCR) | Lee texto de imágenes y PDFs escaneados |
| **PdfPig** | Librería de lectura de PDF | Extrae texto de archivos PDF |
| **DocX** | Librería de lectura de Word | Extrae texto de documentos .docx |
| **Swagger UI** | Interfaz de pruebas | Permite probar la API desde el navegador |

---

## 8. Estructura del proyecto

```
OllamaIntegrationAPI/
│
├── 📁 Controllers/            ← Reciben las solicitudes del usuario
│   ├── DocumentController     → Extraer información de documentos
│   ├── IngestionController    → Subir documentos legales al sistema
│   ├── AnalysisController     → Analizar contratos
│   ├── QueryController        → Hacer preguntas al sistema
│   └── InnerController        → Verificar estado del sistema
│
├── 📁 Services/               ← La lógica principal del sistema
│   ├── DocumentParserService  → Lee documentos (PDF, Word, imágenes)
│   ├── ChunkingService        → Divide textos en fragmentos inteligentes
│   ├── EmbeddingService       → Convierte texto en representaciones numéricas
│   ├── QdrantVectorStore      → Almacena y busca en la base de datos
│   ├── LLMService             → Se comunica con el modelo de IA
│   ├── AnalysisService        → Ejecuta el análisis de contratos
│   └── OrchestratorService    → Decide qué tipo de análisis hacer
│
├── 📁 Models/                 ← Definiciones de datos
│   ├── QueryRequest           → Estructura de una pregunta
│   ├── AnalysisRequest        → Estructura de una solicitud de análisis
│   ├── AnalysisResult         → Resultado de un análisis de contrato
│   ├── DocumentChunk          → Un fragmento de documento con su metadata
│   └── Response               → Formato estándar de respuestas
│
├── 📁 Helpers/                ← Utilidades auxiliares
│   ├── JsonSanitizer          → Limpia las respuestas JSON de la IA
│   ├── ContextBuilder         → Arma los prompts con contexto legal
│   └── VectorMath             → Cálculos de similitud entre textos
│
├── 📁 Middlewares/            ← Manejo global de errores
│   └── ErrorHandlerMiddleware → Captura errores y devuelve respuestas limpias
│
├── 📁 tessdata/               ← Datos del OCR (idioma español)
├── Program.cs                 ← Punto de inicio de la aplicación
├── Dockerfile                 ← Instrucciones para construir el contenedor
└── docker-compose.yml         ← Orquestación de los 3 contenedores
```

---

## 9. Requisitos para ejecutar

| Requisito | Mínimo | Recomendado |
|---|---|---|
| **Docker Desktop** (Windows con WSL2) | ✅ Obligatorio | — |
| **Git** | ✅ Obligatorio | — |
| **RAM** | 16 GB | 32 GB |
| **Disco libre** | 15 GB | 30 GB |
| **GPU** | No requerida | NVIDIA (acelera la IA de ~60s a ~5s) |

### Tiempos de respuesta esperados

| Operación | Sin GPU (solo CPU) | Con GPU NVIDIA |
|---|---|---|
| Ingesta de un documento | 30-120 segundos | 10-30 segundos |
| Pregunta al sistema | 10-60 segundos | 3-10 segundos |
| Análisis de contrato | 30-120 segundos | 10-30 segundos |
| Extracción de datos | 10-60 segundos | 3-10 segundos |

---

## 10. Ejemplos de uso

### Ejemplo 1 — Subir una ley al sistema

**Solicitud:**
```
POST /api/ingestion/upload

Archivo: ley_aduanas.pdf
Tipo de documento: ley
Fuente: Congreso de la República
```

**Respuesta:**
```json
{
  "statusCode": 200,
  "success": true,
  "data": {
    "chunksIngested": 45
  }
}
```

> Significa que se almacenaron 45 fragmentos del documento para consultas futuras.

---

### Ejemplo 2 — Preguntar sobre normativa

**Solicitud:**
```
POST /api/query

{
  "query": "¿Cuáles son los requisitos para importar alimentos?",
  "topK": 5
}
```

**Respuesta:**
```json
{
  "statusCode": 200,
  "success": true,
  "data": {
    "answer": "Según el Artículo 15 de la Ley de Aduanas, los requisitos para importar alimentos incluyen...",
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

> La IA responde basándose en los documentos que se subieron, citando el artículo exacto.

---

### Ejemplo 3 — Analizar un contrato

**Solicitud:**
```
POST /api/analysis/contract

Archivo: contrato_importacion.pdf
Consulta: Verifica cumplimiento con normativa aduanera
```

**Respuesta:**
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
    "summary": "El contrato presenta 2 riesgos principales relacionados con la falta de penalidades y referencias cambiarias."
  }
}
```

> La IA encontró que el contrato **no cumple** con la normativa, identificó 2 riesgos específicos y citó los artículos legales relevantes.

---

### Ejemplo 4 — Extraer datos de una factura

**Solicitud:**
```
POST /api/document/extract-file

Archivo: factura_comercial.pdf
Instrucción: Extrae número de factura, fecha, monto total y productos
Formato: json
```

**Respuesta:**
```json
{
  "numero_factura": "FAC-2024-001234",
  "fecha": "2024-11-15",
  "monto_total": "$15,230.00",
  "productos": [
    "Café orgánico - 500 kg",
    "Cacao en polvo - 200 kg"
  ]
}
```

> La IA leyó la factura y extrajo exactamente los datos solicitados en formato estructurado.

---

## 11. Integración con Laserfiche

Este sistema está diseñado para integrarse con **Laserfiche**, un sistema de gestión documental empresarial.

### ¿Cómo se conecta?

```
┌──────────────┐         ┌─────────────────────┐         ┌──────────────┐
│              │  HTTP    │                     │  HTTP    │              │
│  Laserfiche  │────────►│  OllamaIntegration  │────────►│  Ollama +    │
│  (Gestión    │  POST   │  API                │         │  Qdrant      │
│  Documental) │◄────────│  (localhost:5000)    │◄────────│  (IA local)  │
│              │  JSON    │                     │  JSON    │              │
└──────────────┘         └─────────────────────┘         └──────────────┘
```

### Flujo típico con Laserfiche

1. **Un documento nuevo** llega a Laserfiche (una factura, contrato, etc.)
2. **Un script de Laserfiche** envía el documento a la API via HTTP
3. **La API procesa** el documento (lo lee, lo analiza contra la normativa, extrae datos)
4. **La API responde** con los datos extraídos en formato JSON
5. **Laserfiche usa** esos datos para llenar campos del documento automáticamente

### Endpoint principal para Laserfiche

```
POST /api/document/extract-file
```

Este endpoint recibe cualquier documento y devuelve la información que se le pida, ideal para automatizar la clasificación y llenado de metadatos en Laserfiche.

---

## 12. Preguntas frecuentes

### ¿Los documentos se envían a internet?

**No.** Todo se ejecuta localmente en tu red. Los modelos de IA corren dentro de contenedores Docker en tu propia máquina. Ningún dato sale de tu infraestructura.

### ¿Necesito GPU?

**No, pero se recomienda.** Sin GPU, las respuestas tardan entre 30 y 90 segundos. Con una GPU NVIDIA compatible, tardan entre 3 y 10 segundos.

### ¿Qué idiomas soporta?

El sistema funciona en **español e inglés**. El reconocimiento óptico (OCR) está configurado para español. Los modelos de IA (Mistral, nomic-embed-text) entienden ambos idiomas.

### ¿Qué tipos de archivo acepta?

| Tipo | Extensiones | Notas |
|---|---|---|
| PDF | `.pdf` | Texto nativo o escaneado (OCR automático) |
| Word | `.docx` | Con imágenes embebidas (se aplica OCR) |
| Imágenes | `.png`, `.jpg`, `.jpeg`, `.bmp` | Se aplica OCR |
| TIFF | `.tiff`, `.tif` | Soporta múltiples páginas |

### ¿Qué pasa si apago Docker?

Los **documentos almacenados no se pierden**. Tanto la base de datos como los modelos de IA se guardan en volúmenes persistentes. Solo se borran si explícitamente se ejecuta un comando de limpieza con la opción `-v`.

### ¿Puedo cambiar el modelo de IA?

Sí. Los usuarios pueden especificar qué modelo usar en cada solicitud. El modelo por defecto es **Mistral 7B**, pero se puede usar cualquier modelo disponible en Ollama.

### ¿Cuántos documentos puedo almacenar?

No hay límite artificial. La cantidad depende del espacio en disco disponible. Cada documento típico (ley, regulación) genera entre 20 y 100 fragmentos que ocupan muy poco espacio.

### ¿La IA puede equivocarse?

Sí, como cualquier IA. Sin embargo, al trabajar con RAG (Generación Aumentada por Recuperación), las respuestas están **fundamentadas en documentos reales** y se proveen las fuentes, lo que permite verificar la información. La IA tiene instrucciones de no inventar datos y de indicar cuando no tiene suficiente información para responder.

---

> 📌 **Nota:** Para instrucciones técnicas detalladas de instalación y despliegue, consulte el archivo [DEPLOYMENT.md](DEPLOYMENT.md). Para documentación técnica completa del código, consulte [DOCUMENTATION.md](DOCUMENTATION.md).
