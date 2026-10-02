// CARTO's dark_all tiles started requiring an API key (every tile came back watermarked
// "API KEY REQUIRED"), so the maps use standard OSM tiles, darkened in CSS (`.map-tiles-dark`
// in global.css). The filter sits on the tile layer only, so markers keep their real colours.
export const MAP_TILE_URL = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png'
export const MAP_TILE_ATTRIBUTION =
  '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
export const MAP_TILE_CLASS = 'map-tiles-dark'
