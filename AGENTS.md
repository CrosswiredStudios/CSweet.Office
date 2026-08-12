# C-Sweet Satellite Office contributor instructions

- This repository is an independently versioned installable deliverable. Do not couple its tags to C-Sweet headquarters tags.
- If `CSweet.SatelliteOffice.Contracts` changes, bump that package using semantic versioning, pack it, update this repository and C-Sweet to the same released pin, and verify both with local project references disabled.
- Never publish or sign from an ordinary development runner. Release signing and certification require the hardened platform workflows.
- Preserve Satellite Office identity on upgrades only after the office is drained and has zero active assignments. A first install removes legacy Execution Node services but always enrolls a fresh identity.
