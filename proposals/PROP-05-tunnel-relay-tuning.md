# PROP-05 — Tune the guest tunnel relay

**Status:** proposal — not implemented. **Area:** speed. **Effort:** medium.

**Tooling cost:** payload rebuild for the host-side changes. The optional guest-side stage is a guest-image
fingerprint change: it forces a Packer rebuild and re-certification.

**Invariant interactions:** none required. The proposal preserves the tunnel's binding rule (office id,
assignment id, fencing epoch), the empty `Sequence = 0` opening frame, and the strict sequence-contiguity and
epoch-match validation on download. Numeric frame limits are governed by `SEC-INV-11`, so changing defaults is
a security review, not a tuning change; changing pump buffer sizes below the negotiated maximum is not.

## Problem

Every guest byte crosses three hops: guest vSock → RuntimeHost → Node (local pipe) → Headquarters (gRPC), and
back. The two pumps are fixed at 64 KiB and the per-frame construction repeats identifiers:

- `OfficeWorker.RelayGuestChannelAsync` reads the guest stream into a 64 KiB buffer, copies it into a new
  `ByteString` per frame, and recomputes `state.OfficeId.ToString("D")` (plus assignment id, fencing epoch,
  and session epoch) for every frame. Both directions run inside `Task.Run`.
- `RuntimeHostRpcServer.OpenGuestChannelAsync` pumps with `CopyToAsync(…, 64 * 1024)` in both directions.
- The guest writes one envelope per broker response (`GuestBrokerSession`), and reads with
  `Math.Min(options.MaximumFrameBytes, lease.MaximumFrameBytes)`.
- Builder workloads upload artifact bundles in 768 KiB chunks (`BuilderGuest.ChunkBytes`), so each chunk
  becomes roughly twelve tunnel frames; a 300 MiB bundle becomes about 4,800 frames.

The framing is correct and observable; the cost is per-frame overhead (allocation, protobuf copy, identifier
formatting, gRPC message) multiplied by a small buffer size.

## User story

**Scenario.** A builder workload finishes compiling and uploads a 300 MiB artifact bundle through the local
broker. The build's own CPU time is minutes; the upload stalls for long enough to be noticed, and during the
stall the Node — an unprivileged process doing nothing but relaying — shows meaningful CPU from constructing
thousands of small frames.

> As a builder customer, I want an in-guest bundle upload to keep up with the guest's local broker, so that
> build wall-clock is dominated by compilation rather than by relay overhead.

## Recommended fix

### Stage 1 — Host-side pump tuning (payload only)

1. Raise the pump buffers from 64 KiB to a configured size bounded by the smaller of
   `RuntimeHostEndpointOptions.MaximumFrameBytes` and the lease's `MaximumFrameBytes` (start at 256 KiB).
2. Hoist the constant frame fields (office id, assignment id, fencing epoch, session epoch) so they are
   formatted once per relay, not once per frame.
3. Reuse buffers (`ArrayPool<byte>`) instead of allocating a new buffer per direction, and consider
   `ReadOnlySequence`-aware writes where the transport allows it.

### Stage 2 — Guest-side coalescing (guest change; fingerprint and certification)

If measurements still show a bottleneck after stage 1, let the guest merge small broker responses into fewer
envelopes and raise its own read buffer, staying within the lease's `MaximumFrameBytes`. This changes guest
code under a fingerprint root, so it forces a guest rebuild and re-certification — plan it as a separate
change, not part of stage 1.

Do **not** reconsider the topology. The RuntimeHost never reaches the network
([`docs/10-system/02-trust-model.md`](../docs/10-system/02-trust-model.md)); the Node is the relay by design.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Node/OfficeWorker.cs` (`RelayGuestChannelAsync`) | Larger buffer, hoisted identifiers, pooled buffers | Keep the explicit empty `Sequence = 0` frame and its comment |
| `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRpcServer.cs` (`OpenGuestChannelAsync`) | Match the pump buffer size | Same 64 KiB constant today |
| Provider guest transports (`HyperVSocketTransport`, Firecracker/Apple connectors) | Verify their internal buffers do not cap below the new pump size | Only if measurements justify |
| Tests | Extract the frame-construction logic if possible and unit-test sequence/epoch validation with larger frames | Keep `ExternalPlatformStdioGuestChannelConnectorTests` green |
| Docs | [`docs/30-workloads/04-guest-broker-protocol.md`](../docs/30-workloads/04-guest-broker-protocol.md), [`docs/80-reference/wire-protocols.md`](../docs/80-reference/wire-protocols.md) if frame sizes are documented | Payload rebuild required |

## What must not change

- The opening empty frame (`Sequence = 0`) and its deadlock rationale.
- Strict download validation: contiguous sequence from `0`, matching fencing epoch.
- The tunnel's binding to office id, assignment id, session epoch, and fencing epoch.
- RuntimeHost's no-network property.
- The `SEC-INV-11` clamps on `MaximumFrameBytes`; do not raise defaults without a review.

## Verification

1. Measure builder artifact upload wall-clock and Node CPU before and after with the same bundle.
2. Confirm sequence validation still rejects a gap and a mismatched epoch (existing tests).
3. Confirm the guest boot handshake and the `Sequence = 0` frame behaviour are unchanged end to end.

## Sources

`src/CSweet.Office.Node/OfficeWorker.cs`, `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRpcServer.cs`,
`src/CSweet.Office.RuntimeGuest/{GuestBrokerSession.cs,GuestServiceOptions.cs}`,
`src/CSweet.Office.BuilderGuest/Program.cs`, `docs/30-workloads/{04-guest-broker-protocol.md,07-provider-backends.md}`,
`docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
