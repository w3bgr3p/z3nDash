/**
 * XML operation utilities for applying changes to ZennoPoster XML.
 * Operations use line-based paths (e.g., "line:5" means line 5).
 */

/**
 * Insert XML fragment at specified path.
 * @param xml - Original XML string
 * @param path - Path where to insert (format: "line:N")
 * @param fragment - XML fragment to insert
 * @returns Modified XML string
 */
export function applyInsert(xml: string, path: string, fragment: string): string {
  const lineNum = parseInt(path.replace('line:', ''), 10);
  const lines = xml.split('\n');

  // Insert fragment at specified line (0-indexed)
  if (lineNum > 0 && lineNum <= lines.length + 1) {
    lines.splice(lineNum - 1, 0, fragment);
  }

  return lines.join('\n');
}

/**
 * Replace XML at specified path with fragment.
 * @param xml - Original XML string
 * @param path - Path to replace (format: "line:N")
 * @param fragment - XML fragment to replace with
 * @returns Modified XML string
 */
export function applyReplace(xml: string, path: string, fragment: string): string {
  const lineNum = parseInt(path.replace('line:', ''), 10);
  const lines = xml.split('\n');

  // Replace line at specified position (0-indexed)
  if (lineNum > 0 && lineNum <= lines.length) {
    lines[lineNum - 1] = fragment;
  }

  return lines.join('\n');
}

/**
 * Delete XML at specified path.
 * @param xml - Original XML string
 * @param path - Path to delete (format: "line:N")
 * @returns Modified XML string
 */
export function applyDelete(xml: string, path: string): string {
  const lineNum = parseInt(path.replace('line:', ''), 10);
  const lines = xml.split('\n');

  // Delete line at specified position (0-indexed)
  if (lineNum > 0 && lineNum <= lines.length) {
    lines.splice(lineNum - 1, 1);
  }

  return lines.join('\n');
}
