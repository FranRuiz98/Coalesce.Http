# Instrucciones de Copilot para este repositorio

## Contexto técnico
- Proyecto en C# con .NET 10.
- Mantener compatibilidad con el estilo y convenciones existentes del repositorio.

## Reglas de generación de código
- Priorizar código claro, seguro y testeable.
- Usar `async/await` cuando haya E/S.
- Evitar dependencias innecesarias.
- Añadir comentarios solo cuando aporten contexto no obvio.

## Calidad
- No romper APIs públicas existentes sin justificarlo.
- Mantener advertencias del compilador en cero cuando sea posible.
- Incluir pruebas unitarias para lógica nueva o cambios críticos.

## Formato de respuestas
- Proponer cambios mínimos y acotados.
- Indicar archivos afectados.
- Si falta contexto, pedirlo antes de asumir comportamiento.

## Propósito del proyecto
## Descripción formal del proyecto: **Coalesce.Http**

### 1. Visión general

**Coalesce.Http** es una librería open-source para .NET que extiende el pipeline de **HttpClient** para proporcionar capacidades avanzadas orientadas a arquitecturas distribuidas y microservicios.

Su objetivo es resolver problemas reales que aparecen en sistemas de alta concurrencia cuando múltiples servicios se comunican entre sí mediante HTTP, tales como:

* tormentas de requests duplicadas
* retries inseguros
* latencias de cola (tail latency)
* sobrecarga de servicios backend
* falta de caching HTTP semántico
* dificultad para instrumentar llamadas HTTP

La librería no reemplaza el ecosistema existente, sino que se integra con él. Está diseñada para funcionar sobre:

* IHttpClientFactory
* Polly
* Microsoft.Extensions.Http.Resilience

De esta forma Coalesce.Http añade capacidades avanzadas sin romper compatibilidad con el stack estándar de .NET.

---

# 2. Objetivos del proyecto

El proyecto persigue cuatro objetivos principales:

### 1. Reducir tráfico innecesario

Evitar que múltiples requests idénticas generen llamadas HTTP redundantes.

### 2. Mejorar resiliencia

Complementar los mecanismos de resiliencia existentes con estrategias más inteligentes.

### 3. Optimizar latencia

Reducir la latencia percibida mediante técnicas como hedging o caching HTTP.

### 4. Simplificar adopción

Permitir que las aplicaciones existentes adopten estas mejoras con cambios mínimos.

---

# 3. Principios de diseño

El diseño del proyecto sigue varios principios fundamentales.

### Integración con el ecosistema .NET

Coalesce.Http **no reemplaza** componentes existentes como:

* HttpClient
* Polly

En lugar de ello, actúa como una **extensión del pipeline de HttpClient**.

---

### Adopción sin fricción

La integración se realiza mediante configuración del pipeline:

```csharp
builder.Services
    .AddHttpClient("catalog")
    .AddStandardResilienceHandler()
    .AddSmartHttp();
```

Esto permite incorporar funcionalidades avanzadas sin modificar el código existente.

---

### Modularidad

Las funcionalidades se implementan como módulos independientes que pueden habilitarse o deshabilitarse.

---

### Alto rendimiento

La librería prioriza:

* bajo overhead
* mínimas asignaciones de memoria
* operaciones lock-free cuando sea posible

---

# 4. Problemas que resuelve

## 4.1 Tormentas de requests duplicadas

En sistemas con alto tráfico es común recibir muchas requests idénticas simultáneamente:

```
GET /products/42
GET /products/42
GET /products/42
```

Esto genera múltiples llamadas HTTP redundantes.

---

## 4.2 Retries peligrosos

Retries automáticos pueden duplicar operaciones no idempotentes.

Ejemplo:

```
POST /orders
```

Un retry puede crear múltiples órdenes.

---

## 4.3 Latencia de cola (tail latency)

Algunas requests pueden tardar mucho más que la media, afectando la experiencia del usuario.

---

## 4.4 Uso ineficiente del caching HTTP

Muchas aplicaciones ignoran cabeceras como:

```
Cache-Control
ETag
If-None-Match
```

perdiendo optimizaciones importantes.

---

# 5. Funcionalidades principales

## 5.1 Request Coalescing

La funcionalidad principal del proyecto es **request coalescing**.

Cuando múltiples requests idénticas se ejecutan simultáneamente, Coalesce.Http:

1. detecta que la request ya está en progreso
2. reutiliza la misma ejecución
3. devuelve la respuesta a todos los callers

Resultado:

```
100 requests → 1 HTTP call
```

Esto reduce significativamente el tráfico y la carga en servicios backend.

---

## 5.2 Smart Retry

Coalesce.Http proporciona utilidades para aplicar retries solo cuando es seguro hacerlo.

Ejemplo de reglas:

* permitir retry en métodos idempotentes (`GET`, `PUT`, `DELETE`)
* permitir retry en `POST` únicamente si existe `Idempotency-Key`

La implementación se integra con:

* Polly

---

## 5.3 HTTP Caching inteligente

La librería soporta caching basado en semántica HTTP estándar:

* `Cache-Control`
* `ETag`
* `If-None-Match`

Cuando el TTL expira, la librería realiza una **revalidación condicional** en lugar de descargar nuevamente el contenido.

---

## 5.4 Hedging

Hedging consiste en enviar múltiples requests si una request tarda demasiado.

Ejemplo:

```
Request A → enviada
100 ms después
Request B → enviada
```

Se utiliza la primera respuesta recibida.

Esto reduce significativamente la latencia de cola.

---

## 5.5 Observabilidad

Coalesce.Http integra métricas y trazabilidad mediante:

* OpenTelemetry

Ejemplos de métricas:

```
smarthttp.coalesced_requests
smarthttp.cache_hits
smarthttp.retry_attempts
smarthttp.hedged_requests
```

---

# 6. Arquitectura interna

La arquitectura se basa en un **pipeline de middlewares HTTP**.

Flujo conceptual:

```
Application
   ↓
Coalesce.Http Pipeline
   ↓
HttpClient
   ↓
Remote Service
```

---

## Pipeline interno

El pipeline puede incluir componentes como:

```
MetricsMiddleware
↓
CacheMiddleware
↓
CoalescingMiddleware
↓
RetryMiddleware
↓
HedgingMiddleware
↓
Transport
```

---

## Request Context

Cada request se encapsula en un contexto interno:

```csharp
RequestContext
```

que contiene:

* HttpRequestMessage
* CancellationToken
* metadata
* claves de caching y coalescing

Esto evita recomputar información en cada middleware.

---

# 7. Estructura del proyecto

Propuesta de estructura:

```
Coalesce.Http
│
├─ core
│  ├─ Pipeline
│  ├─ RequestContext
│  └─ Transport
│
├─ features
│  ├─ Coalescing
│  ├─ Caching
│  ├─ Hedging
│  ├─ Retry
│  └─ Observability
│
└─ extensions
   └─ ServiceCollectionExtensions
```

---

# 8. MVP propuesto

El MVP inicial debe centrarse en el núcleo del proyecto.

### Funcionalidades incluidas

* integración con HttpClientFactory
* request coalescing
* pipeline básico
* API de configuración

### Funcionalidades futuras

* caching HTTP
* hedging
* observabilidad
* idempotency helpers

---

# 9. Beneficios para arquitecturas de microservicios

La librería aporta mejoras importantes en sistemas distribuidos:

* reducción del tráfico HTTP
* menor carga en servicios backend
* mejor utilización del caching
* mayor resiliencia ante fallos
* menor latencia percibida

---

# 10. Casos de uso

Coalesce.Http es especialmente útil en:

* microservicios
* API gateways
* servicios de agregación
* sistemas de alta concurrencia
* aplicaciones cloud-native

---

# 11. Alcance del proyecto

El proyecto **no pretende**:

* reemplazar HttpClient
* competir con Polly
* implementar un cliente REST completo

En cambio se enfoca en **optimizar las llamadas HTTP existentes**.

---

# 12. Conclusión

Coalesce.Http propone una capa adicional sobre el stack HTTP de .NET que aporta optimizaciones avanzadas comúnmente utilizadas en sistemas de gran escala.

Al combinar:

* request coalescing
* caching HTTP inteligente
* estrategias avanzadas de resiliencia
* observabilidad integrada

la librería puede mejorar significativamente el rendimiento y la eficiencia de aplicaciones distribuidas sin alterar la arquitectura existente.

---

Si quieres, en el siguiente paso puedo ayudarte a definir también:

* **el README inicial del proyecto para GitHub**
* **la API pública completa**
* **el nombre final (hay uno mejor que Coalesce.Http que podría hacerlo más viral)**.
