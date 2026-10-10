const fs = require('node:fs');
const path = require('node:path');
const { Resvg } = require(process.argv[2] || '@resvg/resvg-js');

const directory = path.join(__dirname, '../src/EmbeddedDebugger.App/Assets/Platforms');
const monochromeColors = { apple: '#E8EDF0', alpine: '#E8EDF0', freebsd: '#C83B40', ubuntu: '#E95420' };
for (const file of fs.readdirSync(directory).filter(file => file.endsWith('.svg'))) {
    const name = path.basename(file, '.svg');
    let source = fs.readFileSync(path.join(directory, file), 'utf8');
    if (monochromeColors[name]) source = source.replace('<svg ', `<svg fill="${monochromeColors[name]}" `);
    const renderer = new Resvg(source, { fitTo: { mode: 'width', value: 96 } });
    fs.writeFileSync(path.join(directory, name + '.png'), renderer.render().asPng());
}
