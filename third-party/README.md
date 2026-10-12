# Upstream license snapshots

These files preserve licenses omitted from the resolved NuGet packages. `upstream-notices.json` records the package, repository commit, original URL and SHA-256 of each snapshot.

The payload build collects license and notice files included in packages, then uses these snapshots only when the package's recorded repository and commit match. A dependency change without a matching license snapshot stops packaging for review. Referenced self-contained .NET runtime downloads are included separately because they do not appear in the normal NuGet library list.

The generated `licenses/third-party/inventory.json` preserves package identities, archive hashes, license expressions, copyright metadata, source URLs and copied notice hashes. The inventory includes resolved build dependencies as well as runtime dependencies; it is not a claim that every listed package's binaries are distributed.

Sources are the exact upstream commits recorded in `upstream-notices.json`. AlphaVSS 2.0.3 uses its packaged Apache-2.0 expression and version-specific license; the current upstream README's MIT description does not replace that packaged version's terms.
