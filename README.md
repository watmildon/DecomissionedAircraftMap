


# DecomissionedAircraftMap

![image](https://github.com/user-attachments/assets/612b113d-9a93-4ff3-9bab-6bf0276926c1)

This repo hosts the style file and image thumbnails for a map of decommissioned aircraft. Primarily these will be either museum display craft or monuments. The location data is sourced from OpenStreetMap while images are pulled from corresponding Wikidata items. The rendering uses  [Ultra](https://overpass-ultra.us/).

You can view the map using the link below.

* [DecomissionedAircraftMap](https://overpass-ultra.us/#map&query=url:https://raw.githubusercontent.com/watmildon/DecomissionedAircraftMap/refs/heads/main/AircraftMap.ultra) using overpass (slower to load but quicker to update)
* [DecomissionedAircraftMap](https://overpass-ultra.us/#map&query=url:https://raw.githubusercontent.com/watmildon/DecomissionedAircraftMap/refs/heads/main/AircraftMap_GeoJSON.ultra) using cached GeoJSON (fast to load but slower to update)

# Adding more aircraft to the map

![image](https://github.com/user-attachments/assets/a1f15a90-47b7-405e-b88b-bff291c3d948)

There are many [historic aircraft](https://wiki.openstreetmap.org/wiki/Tag:historic=aircraft) on OpenStreetMap that lack enough information for the bot here to pull appropriate thumbnails. You can help link things show up on the map by:

* Adding a [model:wikidata](https://wiki.openstreetmap.org/wiki/Key:model:wikidata) tag for an item on OpenStreetMap either from local knowledge or using hints from other tags already on the object (model=, name=, etc). Ex: [model:wikidata=Q185000](https://www.wikidata.org/wiki/Q185000) for a B-17
* Adding a [wikidata](https://wiki.openstreetmap.org/wiki/Key:wikidata) tag if there is a Wikidata item for that specific aircraft. Ex: [wikidata=Q939784](https://www.wikidata.org/wiki/Q939784) for the Spirit of St. Louis.
* Adding images (property P18) to Wikidata items that are linked from OpenStreetMap. The repository generates a list of items needing attention every night in the [missing Wikidata images file](https://github.com/watmildon/DecomissionedAircraftMap/blob/main/wikidataItemsNeedingReview.txt).

You can find aircraft on OpenStreetMap that need Wikidata tags using [this map](https://ultra.trailsta.sh/#map&m=1.09/0.0000/-1.3357&q=NoewrgLgXAVgziAdgXWBAlgWwKbmgJgFZkBuAKAHoKACAcwEMIALbAJ2tezjABsI4yiAO6tgAIibo4EEK3QBjMQF4x9dK3mt6AMwhjUAQiHoA1ugAmjeobGYQ57DyjGzliPX3kq1AA5zEEBxcvPxkeNTy2AFsJEA), featured above. 


<img width="1502" alt="MapComplete-HistoricAircraftLayer" src="https://github.com/user-attachments/assets/2c8f09d0-76dd-4e7d-a215-1c8772ae02c2" />

Another great way to contribute is to use the [Historic Aircraft theme on MapComplete](https://mapcomplete.org/historic_aircraft). A custom theme has been created by [pietervdvn](https://en.osm.town/@pietervdvn).

# Correcting issues

You may encounter instances where the image in question doesn't match the object being represented. This can happen because either the Wikidata tags on OpenStreetMap or the image property in the linked Wikidata item are incorrect. Correcting these will allow the bot to pick up the correct thumbnail the next time it runs.

Some Wikidata items will incorrectly have a diagram or schematic of the aircraft set at the image (P18) when the more appropriate property is schematic (P5555).


# Update cadence

New data in OpenStreetMap is available on the aircraft map about 1 minute after changes are submitted. New thumbnails are pulled once a day using GitHub actions so any changes to Wikidata entries will be reflected after those run.

# See also

The test server for the [OSM-Wikidata Map Framework](https://wiki.openstreetmap.org/wiki/OSM-Wikidata_Map_Framework) now has an [Aircraft layer](https://test.dsantini.it/#-117.17506,32.71370,17.8,blue,overpass_osm_all_wd+wd_direct,maptiler_osm_carto,aircraft,2025)!
