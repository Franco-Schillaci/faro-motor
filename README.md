# faro-motor

Código fuente del **motor de daño de Faro**, un overlay no oficial para AION 2. Es un fork modificado de
[domel1998/Aion2DPSMeter](https://github.com/domel1998/Aion2DPSMeter), licencia **GPL-3.0-or-later**
([LICENSE](LICENSE)).

- Qué cambió respecto al original y de dónde viene cada parte: [LEEME-FARO.md](LEEME-FARO.md).
- El código está en `Aion2DPSMeter-main/` (`src/` el motor, `tests/` las pruebas).
- Compilar (requiere el SDK de .NET 10): `compilar-motor.bat`.
- Descargas de Faro: [faro-releases](https://github.com/Franco-Schillaci/faro-releases).

El motor lee el tráfico de red que el juego manda a la PC (con Npcap). No toca el juego, no lee su memoria ni rompe
ningún cifrado. Igual, las reglas de NCSOFT no autorizan herramientas de terceros: usarlo es un riesgo para la cuenta.
