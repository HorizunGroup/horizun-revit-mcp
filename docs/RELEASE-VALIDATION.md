# Validación de entrega BIM Production

Esta lista define la evidencia necesaria antes de declarar lista una entrega de Horizun Revit MCP. No sustituye las pruebas automatizadas y no convierte una compilación en una prueba viva de Revit.

| Área | Evidencia requerida | Estado de la evidencia |
| --- | --- | --- |
| Flujos de producción | Recurso MCP `horizun://workflows/bim-production`, prompts y pruebas de servidor | Automatizada antes de instalar |
| Modos y protección | Pruebas Core de perfiles/políticas más lectura de `horizun_health` en Revit | Pruebas automatizadas; lectura viva pendiente |
| Acción actual e historial | Una llamada larga observada en el panel de Revit y recibo JSONL correspondiente | Prueba viva pendiente |
| Workshared audit-only | Modelo colaborativo: ensayo admitido y posible escritura rechazada por política | Prueba viva pendiente; requiere modelo adecuado |
| QA/QC Excel | Escritor local: respaldo, relectura de celdas y plantilla renderizada | Automatizada/artefacto; ejecución de flujo vivo pendiente |
| Coordinación disciplina | Auditoría real de Arquitectura/Estructura, MEP y concreto con cobertura explícita | Prueba viva pendiente; requiere modelos y requisitos aprobados |
| Instalación | `install.ps1`, lectura de vuelta de binarios y `horizun_health` con hashes coherentes | Prueba viva pendiente |
| Material visual | Capturas de panel, historial, salud y un flujo real sin datos sensibles | Pendiente de prueba viva y aprobación del modelo |
| Logs centralizados | Endpoint, autenticación, retención y prueba de envío aprobados por la organización | Pendiente de decisión de infraestructura |
| MSI corporativo | Paquete MSI o aceptación explícita del instalador silencioso existente, probado en equipo limpio | Pendiente de decisión de despliegue |

## Regla de cierre

La prueba viva se ejecuta **solo después** de terminar los cambios de código, documentación y empaquetado. Se debe usar un modelo autorizado, registrar versión y commit, conservar los recibos y reportar cualquier cobertura incompleta como pendiente, no como éxito.
