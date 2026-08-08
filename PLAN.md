A desktop application: DeskLayer

That render javascript plugins to wallpaper or each plugin render to standalone float window

A manager to drag items from left panel to the right area (a virtual overview of current desktop) to help layout items

Items can be click to modify the properties, (layout samilar to xcode)

left panel|edit area|right panel

plugin1 |              [item2]  |props
plugin2 |  [item1]              |
plugin3 |                       |
...     |                       |


```
let properties = [
{"name": "from", "valueType": "string", "value": "openai"},
{"name": "fps", "valueType": "number", "value": "30"}
]

function render(ctx) {
    // here to draw the [item], at fps
}

plugin.export = {
    properties,
    render
}
```

