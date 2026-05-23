/**
 * XML Diff Utilities
 * Computes differences between XML strings and generates HTML visualization
 */

export interface XmlDiff {
  oldXml: string;
  newXml: string;
  changes: XmlChange[];
}

export interface XmlChange {
  type: 'insert' | 'delete' | 'modify';
  path: string; // XPath-like path to changed element
  oldValue?: string;
  newValue?: string;
}

/**
 * Compute differences between two XML strings
 * Currently implements a simple line-based diff algorithm
 * Can be enhanced with proper XML tree diffing if needed
 */
export function computeXmlDiff(oldXml: string, newXml: string): XmlDiff {
  const changes: XmlChange[] = [];

  // Simple line-based comparison
  const oldLines = oldXml.split('\n');
  const newLines = newXml.split('\n');

  // Basic diff detection - compare line by line
  const maxLen = Math.max(oldLines.length, newLines.length);

  for (let i = 0; i < maxLen; i++) {
    const oldLine = oldLines[i];
    const newLine = newLines[i];

    if (oldLine === undefined && newLine !== undefined) {
      // Line inserted
      changes.push({
        type: 'insert',
        path: `line:${i + 1}`,
        newValue: newLine
      });
    } else if (oldLine !== undefined && newLine === undefined) {
      // Line deleted
      changes.push({
        type: 'delete',
        path: `line:${i + 1}`,
        oldValue: oldLine
      });
    } else if (oldLine !== newLine) {
      // Line modified
      changes.push({
        type: 'modify',
        path: `line:${i + 1}`,
        oldValue: oldLine,
        newValue: newLine
      });
    }
  }

  return {
    oldXml,
    newXml,
    changes
  };
}

/**
 * Generate side-by-side HTML view of XML differences
 * Left side shows old XML with deletions highlighted in red
 * Right side shows new XML with insertions highlighted in green
 * Modifications shown as red on left, green on right
 */
export function renderDiffHtml(diff: XmlDiff): string {
  const escapeHtml = (str: string) => {
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  };

  const oldLines = diff.oldXml.split('\n');
  const newLines = diff.newXml.split('\n');

  // Build change map for quick lookup
  const changeMap = new Map<number, XmlChange>();
  diff.changes.forEach(change => {
    const lineNum = parseInt(change.path.split(':')[1]) - 1;
    changeMap.set(lineNum, change);
  });

  let html = '<div class="xml-diff-container">';

  // Left side - old XML
  html += '<div class="xml-diff-side xml-diff-old">';
  html += '<div class="xml-diff-header">Before</div>';
  html += '<pre class="xml-diff-content">';
  oldLines.forEach((line, idx) => {
    const change = changeMap.get(idx);
    let className = 'xml-diff-line';

    if (change?.type === 'delete') {
      className += ' xml-diff-line-deleted';
    } else if (change?.type === 'modify') {
      className += ' xml-diff-line-modified';
    }

    html += `<div class="${className}">${escapeHtml(line)}</div>`;
  });
  html += '</pre></div>';

  // Right side - new XML
  html += '<div class="xml-diff-side xml-diff-new">';
  html += '<div class="xml-diff-header">After</div>';
  html += '<pre class="xml-diff-content">';
  newLines.forEach((line, idx) => {
    const change = changeMap.get(idx);
    let className = 'xml-diff-line';

    if (change?.type === 'insert') {
      className += ' xml-diff-line-inserted';
    } else if (change?.type === 'modify') {
      className += ' xml-diff-line-modified';
    }

    html += `<div class="${className}">${escapeHtml(line)}</div>`;
  });
  html += '</pre></div>';

  html += '</div>';

  return html;
}
