# NinjaPricer
ExileCore2 Plugin for instant price checking. Originally made by https://github.com/DetectiveSquirrel/

What does it do?
This plugin downloads public price data.
The data can then be used to price check items such as:
- Currency
- Essences
- Fragments
- Uniques
- Precursor Tablets (normal/magic/rare)

The category list follows the current PoE2 `/poe2/api` contract. PoE1 endpoints and
PoE1-only item mechanics are intentionally not used.

The plugin can also show the overall worth of a stash tab or inventory.

Item that aren't available in the data show 0c as price.
Items that have multiple unique variants show a range between the lowest and highest cost. Precursor Tablets are matched by base name and rarity variant when poe.ninja provides that field.

The background downloader keeps a per-league JSON cache, revalidates cached responses with
ETag when poe.ninja supplies one, and cancels in-flight work when ExileCore2 unloads the plugin.

## Operation logic

`Initialise` discovers the PoE2 league and starts a background reload. The
downloader fetches the 14 exchange and 9 stash categories from poe.ninja's
`/poe2/api`, keeps raw JSON per league, and publishes one complete snapshot.
`CustomItem` maps an item to a PoE2 category; `NinjaPricer.Methods` resolves
exchange/stash prices, including `PrecursorTablets` by base name and
Normal/Magic/Rare variant. Render consumes the snapshot for inventory, stash,
ground, hover, and trade overlays. `GetValue` and `GetBaseItemTypeValue` are
the bridge API used by companion plugins.

Build: **PASS**, classification **CURRENT_WITH_WARNINGS**; live loader/UI smoke
test remains pending. See the [central PoE2 report](../../README.md) and the
[audit](../../../docs/plugins/NinjaPricer/AUDIT.md).
