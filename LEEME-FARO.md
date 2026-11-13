# Motor de daño de Faro (fork de Aion2DpsMeter)

Esta carpeta es el **código fuente** del motor de daño que usa Faro. Es una copia **modificada** de
[domel1998/Aion2DPSMeter](https://github.com/domel1998/Aion2DPSMeter), licencia **GPL-3.0-or-later**
(ver `Aion2DPSMeter-main/LICENSE`).

El motor compilado vive en `..\aion2-overlay\motor\`. Faro lo arranca sin ventana, recibe sus datos por
la red local del PC (127.0.0.1) y lo cierra al salir. Faro no incluye este código: es un programa aparte
que se comunica por UDP local.

La captura de red y la interpretación del combate son del proyecto original. La lectura del progreso del
personaje (tickets, energía, tareas) está portada de [AcompanION](https://github.com/Swellshinider/AcompanION)
(licencia MIT, Copyright (c) 2026 Eduardo Leal; el aviso completo está en `Progress/ProgressDecoder.cs`).

## Cambios respecto al original (requisito de la GPL)

Fecha: 26/10/2026.

- **Nuevo** `src/Aion2DpsMeter.Core/FaroBridge.cs`: envía la pelea que muestra el medidor, su estado de
  captura, si hay versión nueva y la lista del grupo (clase, nivel, gear score y combat power), como JSON por UDP **solo a 127.0.0.1** (puerto 47321), ~4 veces por
  segundo, más el resumen de cada pelea guardada. Escucha en 127.0.0.1:47322 los comandos `reset` y `quit`.
- `src/Aion2DpsMeter.App/App.xaml.cs`:
  - crea el `FaroBridge`, envía la pelea en cada refresco y cada pelea guardada; el reset de Faro reinicia el medidor;
  - nueva opción `--faro`: funciona como motor de Faro, **sin panel, sin ícono en la bandeja, sin atajos
    propios y sin ventanas de aviso** (los errores se informan a Faro); se cierra con el comando `quit`;
  - nueva opción `--parent-pid N`: se cierra solo si el proceso N (Faro) ya no existe.
- `src/Aion2DpsMeter.Core/Combat/CombatStore.cs` y `Encounter.cs`: **pelea nueva al cambiar de objetivo**
  (opciones `SplitOnTargetChange`, por defecto activada, y `TargetSwitchGapMs`, 2 s). Cuando *vos* le pegás
  a un objetivo que la pelea actual no tiene, empieza una nueva si: todo lo que peleabas murió, o dejaste de
  pegarle hace 2 s o más. No corta si hay un boss vivo (adds y fases son parte de la pelea), con daño en área
  (varios objetivos a la vez) ni por DoTs.
- `CombatStore.cs` (`SetPartyRoster`) y `App.xaml.cs`: cada lista de grupo recibida se anota en el log del
  motor (`%LOCALAPPDATA%\Aion2DpsMeter\logs\meter.log`, línea «party roster»), para diagnosticar miembros sin CP.
- `PartyRosterParser.cs`: el CP de cada miembro se busca tras el número de servidor repetido; si ahí aparece otro
  servidor que el de su id (28/10/2026: id 2404, lista 2405), se usa el primer número de servidor de esa zona.
  Antes la lista se cortaba en ese miembro y él y los que venían después quedaban sin CP toda la mazmorra. El
  servidor del jugador sigue siendo el de su id. Prueba en `tests/RosterTests.cs`.
- `CombatStore.NotATargetLocked`: cuando el motor se entera de que una entidad es un jugador (o que sos vos), deja de
  ser objetivo de la pelea en curso. Con Faro abierto a mitad de mazmorra (28/10/2026), una cura o un golpe sobre vos
  leído como daño antes de que el motor te conociera te dejaba como «objetivo», y la pelea guardada no tenía tu fila
  (3,2M de daño al jefe afuera). Tampoco cuenta tu muerte como la de un monstruo. Pruebas en `tests/LateStartTests.cs`.
- **Nuevo** `src/Aion2DpsMeter.Core/Progress/` (`ProgressDecoder.cs`, `ProgressTracker.cs`), portado de AcompanION
  (MIT): lee del lado servidor → cliente tu personaje (`33 36`), combat power (`56 36`), gear (`1D 56`), contadores de
  tickets y energía (`0B 61` / `0C 61`), Suministros de emergencia (`56 E2` / `3E E2`), kinah ganado hoy (`A5 FF` /
  `A6 FF`), misiones de servicio (`1C 8D`) y Daeva Pass (`24 E3`). Los datos de la entrada al mundo llegan antes que
  el nombre del personaje: se guardan por conexión y se asignan al nombre que sigue.
- Combat power (`56 36 <u64 CP><u64 otro número>`): se lee el **primero**. AcompanION leía el segundo, y Faro mostraba
  204.769 cuando el juego decía 175,0K (13/11/2026). Al entrar al mundo llega varias veces y el primero sube mientras
  carga el equipo (131.793 → 144.404 → 174.579 el 12/11/2026); el segundo quedó fijo en 204.769 y el 28/10 terminó igual
  al primero, así que es otra cosa (¿el máximo?). Prueba con esas tramas reales en `ProgressTests.cs`.
- `Opcodes.cs`: esos códigos, editables en `opcodes.json` como los demás. `PacketParser.cs`, `CaptureDispatcher.cs`,
  `MeterEngine.cs`: pasan cada mensaje al lector nuevo. `FaroBridge.cs`: mensaje `progress` a Faro al cambiar algo
  (y cada 30 s). `App.xaml.cs`: lo conecta y anota en el log las líneas «progress:». `JobClass.cs`: códigos de clase
  2–4 (clases base) y 45–48 (Luchador). Pruebas en `tests/.../ProgressTests.cs`.
- `Progress/`: el segundo número de cada contador (bit 3 de la máscara, que AcompanION llama «charged» y no usa) ya
  no se descarta: va en el log como `4=2(+2)` y en el mensaje `progress` como `extra`. Lo identificamos nosotros
  (27/10/2026) comparando con el juego: es lo extra (crafteado o ganado) que el juego muestra en celeste, como en
  «2(+2)/14»; el juego gasta primero lo normal. `ProgressTracker.LogCounterBytes` (apagado) anota los bytes de cada
  mensaje de contadores (`0B 61` / `0C 61`, línea «progress: bytes»), por si hace falta revisar otro dato.
- `Progress/` + `Opcodes.cs`: la colección de mascotas de la cuenta, portada de AcompanION (MIT): `00 90` al entrar al
  mundo (mascota que llevás, nivel y almas de cada una), `0D 90` almas ganadas y `14 90` subida de nivel. Se guarda una
  sola vez (es igual para todos los personajes) y va a Faro como mensaje `pets` (al cambiar y cada 30 s); el log anota
  las líneas «progress: pets» / «progress: pet …».
- `CombatStore.MobKilled` + `FaroBridge.SendKill`: cada monstruo que golpeaste vos o tu grupo y muere va a Faro como
  mensaje `kill` con su código (`npc`). Faro busca a qué mascota le da almas y, si ya está al máximo, lo avisa en el
  overlay de mascotas (el juego no manda almas de mascotas completas). Cada muerte queda en el log («kill: npc …», o
  «kill: entity … without npc code» si el monstruo apareció antes de que arrancara el motor). Pruebas en `tests/KillTests.cs`.
- `ProgressTracker.PetsGainedBeforeCollection` + `FaroBridge.SendPetsDelta`: si el motor arranca con el juego ya en el
  mundo no tiene la colección (llega al entrar al mundo); las almas y subidas de nivel de ese rato van a Faro como
  mensaje `petsdelta` (cantidades, no totales) y Faro las suma a la colección que tenía guardada.
- Puntos de Abismo: `AB FF <u64 que tenés><u64 tope>` (`AbyssPointsEvent`, gains `abyss_points`/`abyss_limit` en el
  mensaje `progress`). AcompanION lo había visto como `A9 FF` en una versión anterior; una captura del 28/10/2026 mostró
  `AB FF` subiendo 38.998 → 39.014 con el cartel «Puntos de Abismo x16» (tope 2.500.000). El log anota «progress:
  kinah today N» y «progress: abyss points N (limit M)», y los bytes de `A7 FF`…`AC FF` (hasta 200 por sesión,
  «currency bytes») por si cambian de nuevo.
- Kinah que tenés: el kinah es un ítem (930100031); su cantidad llega en la actualización de ítems `1B 56 <u8><u8><u8>
  <u32 ranura><u32><u32 ítem><u64 cantidad>…` (`KinahHeldEvent`, gain `kinah_held`, log «progress: kinah held N»).
  Encontrado el 28/10/2026: subía exactamente lo que el juego mostraba como «Kinah xN» en cada muerte. El «kinah ganado
  hoy» (`A5/A6 FF`) no cambia con el kinah de los monstruos.
- Arranque con el juego ya en el mundo: el progreso de esa conexión espera la identidad (`33 36`), que solo llega al
  entrar al mundo; `ProgressTracker.BindByName` lo asigna antes al nombre que ya conoce el lado del daño
  (`CombatStore.KnownLocalName`), nunca después de una entrada al mundo en esa conexión. Las muertes de monstruos que
  aparecieron antes de que arrancara el motor van a Faro como `kill` con `npc` 0.
- PvP (`Pvp/PvpTracker.cs` + `FaroBridge.SendDeath`, mensaje `death`): cuando otro jugador te mata, quién fue y quién
  más te pegó en los 20 s anteriores. Solo lee lo que el servidor le manda al cliente (sin tocar el juego ni el cifrado):
  - el registro de muerte `04 8D <víctima><4 bytes><asesino varint><u16 servidor><len><nombre><len><legión>` (el mismo
    que ya nombraba al dueño del botín; para un jugador nombra a quien lo mató);
  - la aparición `45 36 … <len><nombre><u32 clase>` y, más adelante, el equipo que el servidor muestra:
    `<u32 ítem><u8 +N><u8 ranura><8 ceros>` (ranuras 1 arma, 2-8 armadura, 9 collar, 10-11 aros; el primer dígito del
    ítem tiene que coincidir con la ranura: 1, 2 o 3). Si el mismo jugador vuelve a aparecer dentro de otro registro sin
    clase ni equipo, se mantiene lo de la aparición completa;
  - los golpes que recibís (`04 38`/`05 38`) de entidades que son jugadores;
  - `4A 36 <entidad>`: llega solo sobre vos. Tu número de entidad cambia en algunos teletransportes sin un nuevo registro
    propio (28/10: el registro decía 15724 y al morir en el Abismo eras 15580), así que PvP usa este para saber quién
    sos; antes del primero, el del lado del daño.
  Probado con las dos muertes del 28/10/2026 (dos jugadores de Lumiel). Pruebas en `tests/PvpTests.cs`. El log anota
  «pvp: you are entity N» y «pvp: killed by …».
- `App.xaml.cs`: con `--faro` el motor no busca versiones nuevas del proyecto original (Faro se actualiza solo, con el
  motor adentro, desde sus propias versiones).
- `CombatStore.OrphanCasters` (al armar cada pelea): efectos en el suelo que nadie ató a quien los lanzó. El juego crea
  una entidad para Fire Wall, Bittercold Wind, Divine Aura o los espíritus, y el motor la ata a su dueño por la
  aparición (nombre del lanzador, bloque del dueño) o por el registro que lo nombra. Cuando eso se pierde quedaban
  filas «#número»: en Fire Temple el 01/11/2026, cuatro (Bittercold Wind ×2 y Fire Wall del hechicero del grupo,
  Divine Aura de su clérigo) mientras otros de las mismas habilidades se ataban bien. Una entidad así no tiene nombre y
  pega con una sola habilidad (una base: código / 10 000), la de quien la lanzó: va al único jugador con nombre de la
  pelea que también usó esa habilidad (directo o por otros efectos ya atados); si nadie la usó y apareció como efecto
  (`41 36`), al único jugador con nombre de esa clase. Si hay dos posibles queda como está. Un desconocido nunca se
  mueve: usa más de una habilidad, y una aparición `41 36` nunca es un jugador. Pruebas en `tests/OrphanEffectTests.cs`.
- Enemigos cerca (`Pvp/NearbyTracker.cs`, `PvpTracker.Nearby` + `FaroBridge.SendNearby`, mensaje `nearby`): los
  jugadores que el servidor te está mostrando, con servidor, legión, clase, CP y +N del arma. Faro elige los enemigos
  (servidores de la otra facción). Se manda cuando la lista cambia (se mira cada 250 ms) y cada 5 s. Sale de:
  - la aparición `45 36`, que además del equipo trae
    - servidor y legión: con legión `<u16 servidor><6 bytes><u16 servidor><len><legión>`; sin legión
      `<u16 servidor><4 bytes><varint de 3 bytes> FF×8 80 75 D5 2A BB 03` (el servidor queda 9 bytes antes de esa marca);
    - el CP cerca del final: `<u32 500-9999><u32 1-99>[un byte 1-15]<u32 CP>`. Se lee solo cuando se sabe dónde termina
      la trama. Comprobado el 30/10/2026: el CP de un jugador en la aparición = el de su ficha oficial ese día;
  - `47 36 <entidad> 01 00`: esa entidad salió de tu vista (en las capturas fue lo último que llegó de 34 jugadores que
    se alejaron; los 20 que volvieron trajeron una aparición nueva);
  - el cambio de zona (`23 36 00`), que vacía la lista.
  Con las 9 capturas del 28-30/10: 78 de 78 jugadores con servidor y CP. El servidor y la legión de la aparición también
  van a PvP, así los que te pegaron sin matarte salen con su servidor. Pruebas en `tests/NearbyTests.cs`.
  No hay vencimiento por tiempo: si el `47 36` se pierde, el jugador queda en la lista hasta el próximo cambio de zona
  (13/11/2026 en el Abismo; resucitar no es cambio de zona). En las 15 capturas guardadas las 77 salidas llegaron al
  principio de un mensaje (las lee bien); falta una captura del Abismo para saber la causa.
- Paquetes perdidos (`TcpStream.BytesLost` + `CaptureDispatcher.Trace`): cuando a la captura le falta un pedazo del
  tráfico del juego (Npcap no lo entregó) y el motor deja de esperarlo (500 ms), el log anota «capture: packets lost:
  N gap(s), M bytes missing, the first at HH:MM:SS», sumado y como mucho una vez por minuto. Lo que venía ahí se
  perdió: un golpe o el «salió de tu vista» de Enemigos cerca. Los primeros 5 s después de engancharse a la conexión no
  cuentan (el motor saltea lo que la búsqueda no le pasó). Antes se salteaba sin dejar rastro. Pruebas en
  `TcpStreamTests.cs` y `DispatcherTests.cs`.

## Compilar / actualizar el motor

1. SDK de .NET 10 (una vez): `winget install Microsoft.DotNet.SDK.10`
2. Cerrar Faro y hacer doble clic en `compilar-motor.bat`. Deja el resultado en `..\aion2-overlay\motor\`.

Cuando el juego se actualiza, el proyecto original suele publicar una versión nueva (los opcodes cambian).
Para actualizar: bajar el código nuevo, volver a aplicar los cambios de arriba y compilar.

## Aviso

Como cualquier medidor de daño, no es oficial ni está aprobado por NCSoft; los términos del juego podrían
no permitirlo. Es riesgo de cuenta.
