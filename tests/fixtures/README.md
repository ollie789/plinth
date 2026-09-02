# Golden fixtures

One real pack shot per retailer CDN host the engine handles, pulled from the
live feed by `tools/fetch-fixtures.mjs`. `sources.txt` records the URLs.
`golden/` holds the expected result for each (written by the golden tests
with `PLINTH_UPDATE_GOLDEN=1`).

These images are retailer property used here solely as test inputs for an
image-processing tool and are not redistributed for any other purpose.
Re-fetch rather than edit; if a host changes its imagery, refresh the file
and regenerate its golden record.

A new fixture needs one run with `PLINTH_UPDATE_GOLDEN=1 dotnet test` to
create its golden record; a plain run fails on a missing record by design.

`images-asos-media-com` is a real pack shot whose verdict is `packShot:false`
(light gradient ground); the golden freezes the current, known-wrong label
because the verdict is report-only.
