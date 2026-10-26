# Smart-X IoT Mesh Ecosystem — Part 1 + Part 2

| Project | Role | Default URL |
|---|---|---|
| `SmartX.Api` | ASP.NET Core Minimal API: ingestion, validation, queues, command stream, insights, encrypted file storage | http://localhost:5080 |
| `SmartX.Client` | Blazor Web App (Interactive Server) dashboard that talks to the API over HTTP | http://localhost:5090 |

* **Part 1** – Data Ingestion & Validation Gateway (generics, operator overloading, jagged arrays, recursion, uploads).
* **Part 2** – Real-Time Command Stream & Device Management (queues, priority queue, stack/undo, dictionaries, sets, predictive Action Engine).

## 1. Prerequisites
- **.NET 10 SDK** (or .NET 8: change `<TargetFramework>net10.0</TargetFramework>` to `net8.0` in both `.csproj` files; nothing else changes).
- VS Code + C# Dev Kit. No database needed: everything is in memory and seeded with 8 named mock devices.

## 2. Restore, build, run
```bash
dotnet restore
dotnet build

# terminal 1 (start first)
cd SmartX.Api && dotnet run
# terminal 2
cd SmartX.Client && dotnet run
```
Open **http://localhost:5090**. Docker alternative: `docker compose up --build` (client http://localhost:5090).

## 3. Navigation
The header menu and the landing page offer three modules: **Sensor Ingestion** (Part 1), **Command Stream** (Part 2) and **Network Topology** (disabled – final PoE). Navigation is asynchronous (Blazor SPA routing) and state is kept: filters, selected device and command choices live in the scoped `UiState`, and the device simulator is a scoped `SimulatorService`, so it keeps running when you change page and the pages are exactly as you left them when you return.

## 4. Part 2 – what lives where

| Requirement | Implementation |
|---|---|
| FIFO telemetry queue | `Queue<TelemetryEnvelope>` in `SmartX.Api/Services/StreamEngine.cs` |
| Critical alerts bypass the queue | `PriorityQueue<TelemetryEnvelope,(int,long)>` in `StreamEngine`; drained **before** the FIFO queue |
| Command history + Undo | `Stack<CommandRecord>` in `StreamEngine` (`IssueCommand`, `UndoLast`) |
| Live devices by MAC (O(1)) | `Dictionary<string, DeviceLiveState>` in `StreamEngine`; `Dictionary<string, SensorProfile>` in `TelemetryStore` |
| Timestamped history | `SortedDictionary<DateTimeOffset, SensorReading>` per device in `StreamEngine` |
| Unique errors / disconnects | `HashSet<string>` (active errors, disconnected nodes); `Add()` returning `false` = duplicate suppressed |
| Background processing | `TelemetryProcessorService` (drains 10 packets per 100 ms; watchdog every second) |
| Predictive Action Engine | `SmartX.Api/Services/ActionEngine.cs` (Markov chain over alert/search/command events) + trend projection and problem-device ranking in `StreamEngine.GetInsights` |
| Search / filter by category and alert state | `GET /api/stream/devices?q=&category=&state=`, UI filter bar |
| Manual override | Command panel + per-device buttons; target a single actuator or `ALL_ACTUATORS`; `On / Off / Toggle` |

Thresholds: moisture < 20 or > 100 = Critical, < 30 or > 80 = Warning; power > 500 W = Critical, > 350 W = Warning. A device silent for 20 s (after it has sent a live packet) is marked **Disconnected**.

Devices only talk to the API: the simulator POSTs batches to `/api/telemetry/batch` exactly like an ESP32 would, and commands change state through the API.

## 5. How to test the queues and dictionaries

Open **Command Stream**, click **Start device simulator**, then:

1. **Dictionary (O(1) lookup by MAC):** type `AA:BB:CC:00:00:04` (or `pump`) in the search box. Each packet in the pipeline finds its device by MAC in one lookup. Registering a duplicate MAC on the Ingestion page is rejected by the same dictionary.
2. **Simulate a high-priority alert:** pick a sensor in the test dropdown and click **Fire critical alert**. The alert appears in *Alerts & Error State* with Lane `PRIORITY`, and the device turns Critical.
3. **Prove the priority queue jumps the FIFO queue:** click **Burst: 150 normal + 1 critical last**. 151 packets are queued; the critical one is sent *last* but the *Processing Order* panel shows it processed *first* (Lane `PRIORITY`; scroll to the bottom of the list, newest first, and note it has the highest Seq), and the alert row says "ahead of N" (the FIFO depth it bypassed). The *FIFO depth* tile jumps to about 150 and drains over ~1.5 s.
4. **HashSet de-duplication:** click **Report disconnect ×3**. The device appears once under *Disconnected nodes* and *Duplicates suppressed* increases by 2.
5. **Watchdog:** click **Mute / unmute device** while the simulator runs; after about 20 s the node is flagged Disconnected. Unmute and it recovers on its next packet.
6. **Stack + Undo:** send `ALL ACTUATORS → On`, then press **Undo last**; every actuator returns to its previous value and the stack depth tile drops.
7. **SortedDictionary history:** click any device row; its timestamped history is shown oldest → newest with a sparkline.
8. **Predictive Action Engine:** fire a critical alert on *Moisture Sensor B*. *Suggested Actions* proposes "Search pump a" / "Turn on Pump A" with a confidence value based on learned patterns (seeded plus your own activity). Each search you make and each command you send teaches the engine. Trend warnings appear when the linear projection of a sensor heads toward its critical limit.

curl equivalents (API on :5080):
```bash
curl -X POST localhost:5080/api/telemetry/float -H "Content-Type: application/json" -d '{"deviceId":"AA:BB:CC:00:00:01","value":9.5}'
curl -X POST localhost:5080/api/commands -H "Content-Type: application/json" -d '{"target":"ALL_ACTUATORS","action":"On"}'
curl -X POST localhost:5080/api/commands/undo
curl localhost:5080/api/stream/stats
curl -X POST localhost:5080/api/stream/disconnect/AA:BB:CC:00:00:03
```

## 6. Part 1 concepts

| Requirement | Location |
|---|---|
| Generics `TelemetryPacket<T>` (struct constraint, no boxing) | `SmartX.Api/Models/TelemetryPacket.cs` |
| Operator overloading | `SmartX.Api/Models/SensorReading.cs` |
| Jagged array → collection pipeline | `TelemetryStore.IngestHistoricalBatch` → custom `TelemetryRingBuffer<T>` |
| Recursion | `RecursiveDeploymentValidator.cs` (server), `FlattenPaths` in `Ingestion.razor` |
| File attachments (AES-256 encrypted at rest) | `POST /api/sensors/{id}/attachments`, `FileEncryptionService.cs`; download via the decrypting endpoint |
| Engagement strategy (gamified pulse grid) | Ingestion page "Live Telemetry Pulse Grid" |

## 7. API reference
`GET /api/health` · `GET/POST /api/sensors` · `GET /api/facility-tree` · `POST /api/telemetry/float|int|bool|batch` · `GET /api/telemetry/recent|buffer-status|aggregate` · `GET /api/stream/stats|devices|history/{mac}|alerts` · `POST /api/stream/disconnect/{mac}` · `POST /api/commands` · `POST /api/commands/undo` · `GET /api/commands/history` · `GET /api/insights` · `POST /api/insights/events` · `POST /api/sensors/{id}/attachments` · `GET /api/sensors/{id}/attachments/{aid}/download`

## 8. Notes
- The AES key in `appsettings.json` is a demo key; use a secret store in production.
- Task 1 report: `SmartX_Part1_Task1_Research_Report.pdf` (convert to Word if your lecturer requires .docx).
- See `COMMIT_GUIDE.md` for a 25+ commit plan.
