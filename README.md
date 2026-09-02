# Plinth

Puts every product on the same plinth. Trims retailer pack shots to their
content and re-centres them on one canvas so a grid of products from many
sellers reads as one catalogue.

Design: `docs/2026-09-02-plinth-design.md`. Build plan: `docs/plans/`.

Requires the .NET 10 SDK. `dotnet test` runs everything.

## Benchmark

`dotnet run -c Release --project src/Plinth.Bench` normalises every fixture
twenty times and compares against `docs/bench/baseline.json`; it exits
non-zero on a regression over 20%. `--write-baseline` resets the baseline
after an intentional change.
