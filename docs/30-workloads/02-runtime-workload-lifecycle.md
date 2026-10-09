# Runtime workload lifecycle

**Audience:** contributors, reviewers, and operators diagnosing a failed assignment.

Workload kind **1**. The workload runs an agent from a materialized artifact, with artifact media attached.

## Sequence

```mermaid
sequenceDiagram
    participant HQ as Headquarters
    participant Node as Node
    participant RH as RuntimeHost
    participant H as Helper
    participant G as Guest

    HQ->>Node: Assignment
    Node->>Node: ValidateAssignment
    Node->>Node: acquire workload slot
    Node->>Node: deserialize specification
    Node->>HQ: DownloadArtifact (bound to office/epoch/digest/token)
    HQ-->>Node: chunks
    Node->>Node: verify SHA-256, build ISO
    Node->>HQ: status Starting
    Node->>RH: CreateWorkloadRequest + SignedWorkloadAuthorization
    RH->>RH: ValidateAndCommit, commit epoch
    RH->>H: create
    H-->>RH: providerInstanceId
    RH->>RH: RegisterHandle (destroy on failure)
    Node->>RH: start
    Node->>RH: OpenGuestChannel
    Node->>HQ: OpenWorkloadTunnel + empty frame Sequence=0
    HQ->>G: boot configuration
    G->>G: mount media, verify digest, extract payload
    G->>HQ: Hello, HostChallenge, Proof, GuestLease
    HQ->>G: StartCommand
    G->>G: setpriv, run entrypoint
    Node->>HQ: status Running
    loop 2 s
        Node->>RH: inspect
    end
    loop 20 s
        Node->>HQ: AssignmentLeaseRenewal
    end
    Note over G,HQ: agent requests proxied over the tunnel
    HQ->>G: ShutdownCommand
    G->>G: stop process, Exit(0), power off
    Node->>HQ: AssignmentStatusUpdate
    Node->>RH: destroy and confirm removal
    RH->>RH: persist destruction confirmation
    Node->>Node: persist pending stop report
    Node->>HQ: AssignmentStopped
    HQ-->>Node: AssignmentStopReceipt
    Node->>Node: retain replay tombstone
```

## Step detail

### 1. Validation

`OfficeWorker.ValidateAssignment` (see
[20-security/04-workload-authorization.md](../20-security/04-workload-authorization.md)) rejects the envelope
and reports `assignment-envelope-invalid` without terminating the session.

### 2. Admission

`_workloadSlots.WaitAsync(...)` against `MaximumConcurrentWorkloads`. This is the only backpressure.

### 3. Specification

`DeserializeSpecification` switches on a `"kind"` / `"Kind"` discriminator with `PropertyNameCaseInsensitive`
and `MaxDepth 32`. Only `RuntimeWorkloadSpecification` and `ToolchainBuildWorkloadSpecification` carry an
artifact digest.

### 4. Artifact acquisition — before the VM exists

If the specification has an artifact digest, the assignment must carry an `ArtifactReadToken` of 32–256
characters, and the Node calls `OfficeArtifactCache.EnsureAsync(gatewayClient, state, assignment, digest, token)`.

**The download and the ISO build complete before any VM is created.** The provider re-verifies the ISO at
create time, and Hyper-V makes and re-verifies a private copy inside the instance directory.

Details in [05-artifacts-and-media.md](05-artifacts-and-media.md).

### 5. Create

`RuntimeHostProviderClient.CreateAuthorizedAsync(specification, ToAuthorization(state, assignment))` sends the
request with the full signed envelope. The gate validates and commits, then the backend invokes the helper.

Status `Starting` is reported **after** the artifact work, not before.

### 6. Start and open the channel

`provider.StartAsync(handle)`, then `IAgentGuestChannelProvider.OpenGuestChannelAsync(handle)`, then a
`RelayGuestChannelAsync` task is started and status `Running` is reported.

### 7. The relay's mandatory first frame

`RelayGuestChannelAsync` opens `OpenWorkloadTunnel` and **immediately writes an empty
`WorkloadTunnelFrame { Sequence = 0, Content = empty, Completed = false }`** before starting either relay
direction.

**Why:** Headquarters cannot start its broker session until the first bound frame arrives, and the Linux guest
waits for Headquarters' boot configuration. If the Node waited for guest bytes before sending anything, the
three parties would deadlock. Omitting this frame hangs every workload.

Upload then uses `Sequence = 1..n` in 64 KiB chunks, ending with `Completed = true` and empty content.
Download asserts `frame.Sequence == expectedSequence++` starting at **0** and a matching `FencingEpoch`, and
breaks on `Completed`. Whichever direction finishes first is awaited; the other is then cancelled.

### 8. Guest-side boot

The guest receives boot configuration, validates it
([20-security/07-guest-isolation.md](../20-security/07-guest-isolation.md)), materializes the artifact by
mounting the media and verifying the bundle digest, then performs the authenticated handshake and starts the
workload under `setpriv`.

### 9. Run

The agent SDK talks HTTP/1.1 to the local broker socket. The guest converts each request into a `ProxyRequest`
with a purpose, and the Node relays it as tunnel frames. Headquarters authorizes by purpose and answers with a
`ProxyResponse`.

Meanwhile the Node inspects every 2 seconds and renews the lease every 20 seconds.

### 10. Completion

Three ways a runtime workload ends:

| Trigger | Result |
|---|---|
| Headquarters sends `ShutdownCommand` | Guest stops the process with a grace period, emits `Exit(0, reason)`, powers off. |
| The process exits on its own | Guest emits `Exit(code, "process-exited", tail)`. |
| The log budget is exceeded | Guest emits `Exit(137, "resource-limit-exceeded")`. |

### 11. Status classification

```
Completed  iff  State != Failed && ExitCode == 0 && TerminationReason in { None, Completed }
Failed     otherwise, with status.ErrorCode ?? "workload-failed"
```

Logs are read on both paths: `ReadLogsAsync` with a 64 KiB cap, decoded as UTF-8, placed in `LogExcerpt`.

### 12. Teardown

`AssignmentStopJournal` persists an exact Office/assignment/epoch record before execution and marks creation intent before `CreateAuthorizedAsync`. The returned handle is persisted before `StartAsync`. Teardown attempts use a ten-second cancellation budget. Failure retains the record and activity marker for recovery; the execution slot/cancellation source are released. A prepared attempt that never entered creation can become a `never_created` report. `RuntimeHostAuthorizationGate.Attempts.cs` stores creation intent after signed authorization validation and retains the returned handle by exact attempt. `RuntimeHostRequestDispatcher.Attempts.cs` serializes creation and teardown, recovers the handle, and persists confirmed shutdown before returning it. For interrupted Hyper-V creation without a returned handle, `HyperVAttemptJournal.cs` retains an opaque attempt key and VM identity; `HyperVHelperController.Attempts.cs` reconciles that exact VM through the existing destroy operation. Missing historical ownership evidence remains unconfirmed rather than becoming invented stop proof. These paths preserve SEC-INV-07, SEC-INV-08, SEC-INV-09, SEC-INV-15 and SEC-INV-20.

`RuntimeHostRequestDispatcher.OperationAsync` inspects the privileged backend before removing handle authorization. A returned destroy request alone is insufficient: inspection must return no workload or `Destroyed`. `RuntimeHostAuthorizationGate.RecordDestroyed` persists an exact-handle confirmation before removing authorization. Duplicate destroy requests can return that confirmation after a lost response or process restart; it cannot authorize starting the workload. These changes preserve SEC-INV-07, SEC-INV-08 and SEC-INV-15.

`OfficeWorker.ReplayStopsAsync` uses existing heartbeat/reconnect discovery, up to eight records with a two-second cancellation budget per pass. Confirmed cleanup produces `AssignmentStopped`; unacknowledged reports are retried. An accepted session-bound `AssignmentStopReceipt` retires delivery while preserving a durable tombstone against assignment replay. `OfficeWorker.ReadControlMessagesAsync` journals every received epoch even when an older epoch still owns execution. A `ReconcileAssignmentStop` hint creates a permanent tombstone for an attempt that never entered creation, or resumes cleanup for an existing record; it cancels only the matching active epoch. Other unresolved epochs keep the activity marker. Startup restores unresolved cleanup markers after normal maintenance-session initialization. Journal and privileged confirmation files are retained; no pruning policy is implemented.

> **Out of repo:** Headquarters owns acceptance and storage of stop evidence. `OfficeWorker.Stops.cs` sends the report and `OfficeWorker.ReadControlMessagesAsync` consumes its receipt through Office.Contracts 0.9.0. Headquarters rediscovers unconfirmed retired attempts; this is a teardown request, never an execution grant. An intact Node journal can prove that an unreceived attempt never entered creation. A legacy `creating` entry with no surviving privileged ownership evidence still requires operator recovery.

## Failure classification

`DescribeExecutionFailure` maps exceptions to stable codes. Tests assert these exact behaviors.

| Exception | Failure code | Detail |
|---|---|---|
| `IsolationUnavailableException` | `isolation-provider-unavailable` | Message passed through verbatim, e.g. *"Hyper-V is not enabled."* |
| `RpcException` `FailedPrecondition` | `headquarters-broker-rejected` | Headquarters detail, control characters stripped, 1500-character cap |
| `RpcException` `PermissionDenied` / `Unauthenticated` | `headquarters-authorization-rejected` | No detail |
| `RpcException` `Unavailable` / `DeadlineExceeded` | `headquarters-unavailable` | No detail |
| Other `RpcException` | `headquarters-rpc-error` | Status code only |
| Anything else | `office-error` | A generic message naming the exception type and telling the reader to use the assignment identifier in the Node service log. The original message is **deliberately not leaked** — a test asserts that a string like `password=do-not-leak` never appears. |

## Sources

`src/CSweet.Office.Node/{OfficeWorker.cs,OfficeWorker.Stops.cs,AssignmentStopJournal.cs,OfficeArtifactCache.cs}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostProviderClient.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostAuthorizationGate.cs}`,
`src/CSweet.Office.RuntimeGuest/{Program.cs,GuestBrokerSession.cs,GuestWorkloadSupervisor.cs,GuestArtifactMaterializer.cs}`,
`tests/CSweet.Office.Tests/{OfficeWorkerFailureTests.cs,RuntimeHostRpcIntegrationTests.cs,AssignmentStopJournalTests.cs}`.

Additional sources: `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.Attempts.cs`, `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRequestDispatcher.Attempts.cs`, `src/CSweet.Office.Runtime.HyperV.Helper/HyperVAttemptJournal.cs`, `src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperController.Attempts.cs`.

Verified: 2026-10-08.
