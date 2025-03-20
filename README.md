
# DecomissionedAircraftMap

This repo hosts the style file and icons for an [Ultra](https://overpass-ultra.us/) based decomissioned aircraft map. You can view the map using the link below.

* [DecomissionedAircraftMap](https://overpass-ultra.us/#map&query=url:https://raw.githubusercontent.com/watmildon/DecomissionedAircraftMap/refs/heads/main/AircraftMap.ultra).

# Adding more aircraft to the map

The map shows thumbnails for each object that is tagged as a [historic aircraft](https://wiki.openstreetmap.org/wiki/Tag:historic=aircraft) and has a suitable wikidata tag from which an image can be pulled. You can add appropriate [Wikidata tags](https://wiki.openstreetmap.org/wiki/Key:wikidata) to already tagged aircraft or add images into linked Wikidata items. Both allow the bot to pick up new images when it runs.

You can find aircraft in OpenStreetMap that need Wikidata tags using [this helpful map](https://ultra.trailsta.sh/#map&m=1.09/0.0000/-1.3357&q=NoewrgLgXAVgziAdgXWBAlgWwKbmgJgFZkBuAKAHoKACAcwEMIALbAJ2tezjABsI4yiAO6tgAIibo4EEK3QBjMQF4x9dK3mt6AMwhjUAQiHoA1ugAmjeqUo0ADnMQQOXXvzJ5q87E7YkgA).

# Correcting issues

You may encounter instances where the image in question doesn't match the object being represented. This can happen because either the Wikidata tag on OpenStreetMap or the image property in the linked Wikidata item are incorrect. Correcting these will allow the bot to pick up the correct thumbnail the next time it runs.


# Update cadence

This is TBD currently but very likely could be run several times a day using appropriate GitHub actions.
