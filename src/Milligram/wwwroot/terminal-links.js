// Coordinates come from terminal cells so wide glyphs and wrapped paths keep their click targets.
export function fileLinks(terminal, open) {
  return {
    provideLinks(y, callback) {
      const buffer = terminal.buffer.active;
      let start = y - 1;
      while (start > 0 && y - start < 8 && buffer.getLine(start)?.isWrapped) start--;
      let text = '', positions = [];
      for (let row = start; row < Math.min(buffer.length, start + 8); row++) {
        const line = buffer.getLine(row);
        if (!line || (row > start && !line.isWrapped)) break;
        for (let column = 0; column < line.length; column++) {
          const cell = line.getCell(column);
          if (!cell || cell.getWidth() === 0) continue;
          if (!cell.getChars() && column === line.length - 1 && buffer.getLine(row + 1)?.isWrapped) continue;
          const chars = cell.getChars() || ' ';
          text += chars;
          for (let i = 0; i < chars.length; i++) positions.push({ x: column + 1, y: row + 1 });
        }
      }
      const paths = /(?:"([^"\r\n]+\.cs)"|'([^'\r\n]+\.cs)'|((?:[A-Za-z]:[\\/]|[\\/]{1,2}|(?:src|tests|\.{1,2})[\\/])[^:\r\n"<>|?*]*?\.cs|(?:[\p{L}\p{N}_@+.-]+[\\/])*[\p{L}\p{N}_@+.-]+\.cs))(?::(\d+)(?::\d+)?|\((\d+)(?:,\d+)?\))/giu;
      const links = [];
      for (const match of text.matchAll(paths)) {
        const from = positions[match.index], to = positions[match.index + match[0].length - 1];
        if (!from || !to || y < from.y || y > to.y) continue;
        const file = match[1] || match[2] || match[3];
        const line = Math.min(2147483647, Math.max(1, Number(match[4] || match[5])));
        links.push({
          text: match[0], range: { start: from, end: to },
          activate(event) {
            if (!(event.ctrlKey || event.metaKey)) return;
            event.preventDefault();
            open(file, line);
          },
        });
      }
      callback(links);
    },
  };
}
