# Request and data flow

**Audience:** contributors and reviewers who need the timeline rather than the mechanism.

Three sequences cover almost everything: enrollment, a runtime workload, and a builder workload. Detailed
mechanisms live in [30-workloads/](../30-workloads/README.md).

## Enrollment

```mermaid
sequenceDiagram
    participant Admin as Administrator
    participant Inst as Installer
    participant Node as CSweet.Office.Node
    participant RH as RuntimeHost
    participant HQ as Headquarters

    Admin->>Inst: run install with control plane URL
    Inst->>HQ: GET api/offices/assignment-trust (TLS pinned by fingerprint)
    HQ-->>Inst: assignment signing key id + SPKI
    Inst->>Inst: write headquarters-trust.json (OfficeId = Guid.Empty)
    Inst->>Inst: create service accounts, ACLs, immutable package
    Inst->>Node: start with enrollment token
    Node->>Node: create self-signed bootstrap certificate (ECDSA P-256, 7 days)
    Node->>HQ: POST api/offices/claim (token, CSR, capacity, provider inventory, posture)
    HQ-->>Node: OfficeId, enrollment receipt, key id, SPKI
    Node->>Node: persist node-state.json, delete enrollment token
    Node->>RH: PinHeadquartersTrust (OfficeId, key id, SPKI)
    Node->>HQ: POST api/offices/{id}/certificate (receipt)
    HQ-->>Node: operational certificate + thumbprint
    Node->>Node: InstallOperationalCertificate (validate, copy key, atomic replace)
    Node->>HQ: Connect (gRPC control stream) with heartbeat + provider inventory
```

The installer pins the assignment key **before** the Office has an identity, writing `OfficeId = Guid.Empty`.
The RuntimeHost accepts exactly one upgrade from the empty office id to the real one; after that the pin is
write-once.

## Runtime workload

```mermaid
sequenceDiagram
    participant HQ as Headquarters
    participant Node as Node
    participant RH as RuntimeHost
    participant Helper as Platform helper
    participant Guest as Guest

    HQ->>Node: Assignment (signed envelope)
    Node->>Node: ValidateAssignment (version, key id, window, digest, signature)
    Node->>HQ: status Starting
    Node->>HQ: DownloadArtifact (assignment/epoch/digest/token bound)
    HQ-->>Node: artifact chunks
    Node->>Node: verify SHA-256, build read-only ISO
    Node->>RH: CreateWorkloadRequest + SignedWorkloadAuthorization
    RH->>RH: ValidateAndCommit (trust, ids, window, digest, signature, replay ledger)
    RH->>Helper: create (typed JSON on stdio, helper digest re-verified)
    Helper->>Helper: New-VM / jailer / VZVirtualMachine
    RH->>RH: RegisterHandle (or destroy the workload if persisting fails)
    Node->>RH: start, then OpenGuestChannel
    RH->>Guest: connect vSock, then raw duplex tunnel
    Node->>HQ: OpenWorkloadTunnel, then empty frame Sequence=0
    HQ->>Guest: boot configuration
    Guest->>Guest: mount artifact media, verify digest, extract payload
    Guest->>HQ: Hello (boot-token HMAC proof)
    HQ->>Guest: HostChallenge (32-byte nonce)
    Guest->>HQ: Proof (ECDSA over challenge payload)
    HQ->>Guest: GuestLease (accepted, expiry, max frame)
    HQ->>Guest: StartCommand
    Guest->>Guest: setpriv, run entrypoint as csweet-workload
    loop every 2 seconds
        Node->>RH: inspect
    end
    loop every 20 seconds
        Node->>HQ: AssignmentLeaseRenewal (+60s requested expiry)
    end
    Guest->>HQ: broker requests (mcp.runtime) relayed by the Node
    HQ->>Guest: ShutdownCommand
    Guest->>Guest: stop process, Exit(0), power off
    Node->>RH: destroy (also removes the handle authorization)
    Node->>HQ: AssignmentStatusUpdate (Completed or Failed)
```

The Node relays bytes but does not construct the boot configuration, does not answer the handshake, and does
not decide whether a request is allowed. Those are Headquarters decisions.

## Builder workload

```mermaid
sequenceDiagram
    participant HQ as Headquarters
    participant Node as Node
    participant Guest as Guest (kind 0)
    participant Builder as BuilderGuest

    HQ->>Node: Assignment with BuilderWorkloadSpecification
    Note over Node: no artifact digest, so no download and no ISO
    Node->>Guest: create, start, open channel, relay tunnel
    HQ->>Guest: boot configuration (no artifact, host-chosen artifact root)
    HQ->>Guest: StartCommand (entrypoint BuilderGuest --repository ... --broker-socket ...)
    Guest->>Builder: launch
    Builder->>HQ: POST /build/fetch (through broker, purpose build.fetch)
    HQ-->>Builder: repository archive chunks
    Builder->>HQ: POST /build/progress (isolate, restore, publish, package)
    Builder->>HQ: POST /build/artifact (chunked bundle upload)
    Builder->>Builder: exit 0
    Guest->>HQ: Exit(0, process-exited)
    Node->>HQ: AssignmentStatusUpdate Completed
```

Builder workloads never attach artifact media — the RuntimeHost rejects `ArtifactImagePath` combined with a
builder spec.

## Control messages

Every inbound `HeadquartersControlMessage` must carry the matching `OfficeId` **and** `SessionEpoch`, or the
session is terminated.

| Message | Node response |
|---|---|
| `Hello` | Key id must match the pinned id and the SPKI must equal the enrolled bytes in fixed time. A mismatch kills the session. |
| `Assignment` | Validate, then either report `Failed` with `assignment-envelope-invalid` and keep the session, or dedupe into the active set, mark the local activity file, and execute. |
| `Fence` | Cancel the assignment's cancellation token. Teardown happens in the handler's `finally`. |
| `Drain` | Set the drain marker file. Nothing else — draining is reported, not enforced, by the Node. |

## Status reporting

The Node reports `Starting`, `Running`, then a terminal `Completed` or `Failed`. `Completed` requires
`State != Failed`, `ExitCode == 0`, and a termination reason of `None` or `Completed`; anything else is
`Failed` with `status.ErrorCode` or `workload-failed`.

Failure codes are deterministic and covered by tests. See
[80-reference/status-and-error-codes.md](../80-reference/status-and-error-codes.md).

## Sources

`src/CSweet.Office.Node/OfficeWorker.cs`, `src/CSweet.Office.Node/OfficeStateStore.cs`,
`src/CSweet.Office.Node/OfficeArtifactCache.cs`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostRequestDispatcher,RuntimeHostAuthorizationGate}.cs`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`src/CSweet.Office.RuntimeGuest/{Program.cs,GuestBrokerSession.cs}`,
`src/CSweet.Office.BuilderGuest/Program.cs`, `scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`.

Verified: 2026-09-15.
