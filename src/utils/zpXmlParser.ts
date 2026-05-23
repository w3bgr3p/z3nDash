/**
 * ZennoPoster XML Parser Utilities
 * Handles parsing and serialization of ZennoPoster template XML format
 */

export interface ZpAction {
  type: string;
  id: string;
  properties: Record<string, string>;
}

export interface ZpTemplate {
  actions: ZpAction[];
}

/**
 * Parse ZennoPoster XML string into structured template object
 * Expected structure: <Project><Actions><Action type="..." id="...">...</Action></Actions></Project>
 */
export function parseZpXml(xml: string): ZpTemplate {
  const parser = new DOMParser();
  const doc = parser.parseFromString(xml, 'text/xml');

  // Check for parsing errors
  const parserError = doc.querySelector('parsererror');
  if (parserError) {
    throw new Error(`XML parsing error: ${parserError.textContent || 'Unknown parse error'}`);
  }

  const actions: ZpAction[] = [];
  const actionElements = doc.querySelectorAll('Actions > Action');

  actionElements.forEach((actionEl) => {
    const type = actionEl.getAttribute('type');
    const id = actionEl.getAttribute('id');

    if (!type || !id) {
      throw new Error('Action element missing required type or id attribute');
    }

    const properties: Record<string, string> = {};

    // Extract all direct child elements as properties
    Array.from(actionEl.children).forEach((child) => {
      properties[child.tagName] = child.textContent || '';
    });

    actions.push({ type, id, properties });
  });

  return { actions };
}

/**
 * Serialize ZpTemplate object back to ZennoPoster XML format
 */
export function serializeZpXml(template: ZpTemplate): string {
  const lines: string[] = [];

  lines.push('<?xml version="1.0" encoding="utf-8"?>');
  lines.push('<Project>');
  lines.push('  <Actions>');

  template.actions.forEach((action) => {
    lines.push(`    <Action type="${escapeXml(action.type)}" id="${escapeXml(action.id)}">`);

    // Add properties as child elements
    Object.entries(action.properties).forEach(([key, value]) => {
      lines.push(`      <${key}>${escapeXml(value)}</${key}>`);
    });

    lines.push('    </Action>');
  });

  lines.push('  </Actions>');
  lines.push('</Project>');

  return lines.join('\n');
}

/**
 * Escape special XML characters
 */
function escapeXml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}
