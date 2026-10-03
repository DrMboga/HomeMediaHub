# HomeMediaHub

A self-hosted family photo and video library. This glossary fixes the words used for its domain
concepts so that docs, code and conversations mean the same thing.

## Language

### Travel

**Journey**:
A stored trip, made of one or more Stops, created by hand or proposed by detection and editable
either way. Its date range runs from its first Stop's start to its last Stop's end; its photos are
every photo taken within that range, by local calendar day — not a hand-picked set. Journeys never
overlap, so a day belongs to at most one Journey.
_Avoid_: Trip (in code and docs), vacation, travel, series

**Stop**:
One place within a Journey, with its own date range. A Journey's Stops never overlap but may
leave gaps (travel days), which still belong to the Journey. Repeat visits are recognised by Stop,
not by Journey.
_Avoid_: Destination, leg, location, waypoint

**Place**:
A named spot on the map that Stops link to, so that visits to it in different years are
recognised as the same place. Can be renamed or merged with another Place (e.g. Nice + Cannes →
"Côte d'Azur").
_Avoid_: Location, city, destination, area

**Journey detection**:
The analysis that turns geotagged photos taken away from home into auto-detected Journeys. Each run
looks only at photos added to the library since the previous run, and never creates a Journey over
days an existing Journey already covers. Its results are ordinary Journeys, labelled as
auto-detected; there is no suggested/confirmed state.
_Avoid_: Trip detection, auto-journeys, suggestions

**Home period**:
A span of time during which the family lived in one area (e.g. Moscow, then Saarbrücken), made of
one or more Home places. Entered by hand. Home periods never overlap; gaps between them mean
"unknown", and the latest may be open-ended.
_Avoid_: Residence, base

**Home place**:
A Place counted as home during a Home period, with its own home radius — e.g. the city flat and
the dacha are two Home places of the same Moscow Home period. Being within the radius of any of
them is being at home, not on a Journey. Because it is a Place, a later trip back to a former home
is recognised as a visit to that same Place.
_Avoid_: Home location, residence, base
