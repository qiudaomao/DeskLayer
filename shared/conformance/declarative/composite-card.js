// realistic widget card composing most node types
let properties = [];
render = () => view([
    VStack([
        HStack([
            Image('thermometer').frame(16, 16),
            Text('Weather').fontSize(13).bold().textColor('#ffffff'),
            Spacer()
        ]).spacing(6),
        Text('21\u00b0').fontSize(34).bold().textColor('#ffffff'),
        HStack([
            Text('H: 24\u00b0').fontSize(11).textColor('#ffffff99'),
            Text('L: 15\u00b0').fontSize(11).textColor('#ffffff99')
        ]).spacing(8)
    ]).spacing(4).padding(14).background('#1c1c1ecc').cornerRadius(16).frame(170, 110)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
