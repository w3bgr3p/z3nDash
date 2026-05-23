/**
 * XML operation utilities for applying changes to ZennoPoster XML.
 * Operations use line-based paths (e.g., "line:5" means line 5).
 */

/**
 * Parse and validate line-based path format.
 * @param path - Path string (format: "line:N")
 * @returns Line number (1-indexed)
 * @throws Error if path format is invalid or line number is invalid
 */
function parseLinePath(path: string): number {
  if (!path.startsWith('line:')) {
    throw new Error(`Invalid path format: ${path}. Expected format: "line:N"`);
  }
  const lineNum = parseInt(path.replace('line:', ''), 10);
  if (isNaN(lineNum) || lineNum < 1) {
    throw new Error(`Invalid line number in path: ${path}`);
  }
  return lineNum;
}

/**
 * Insert XML fragment at specified path.
 * @param xml - Original XML string
 * @param path - Path where to insert (format: "line:N")
 * @param fragment - XML fragment to insert
 * @returns Modified XML string
 * @throws Error if path is invalid or line number is out of bounds
 */
export function applyInsert(xml: string, path: string, fragment: string): string {
  const lineNum = parseLinePath(path);
  const lines = xml.split('\n');

  if (lineNum > lines.length + 1) {
    throw new Error(`Line number ${lineNum} out of bounds (max: ${lines.length + 1})`);
  }

  lines.splice(lineNum - 1, 0, fragment);
  return lines.join('\n');
}

/**
 * Replace XML at specified path with fragment.
 * @param xml - Original XML string
 * @param path - Path to replace (format: "line:N")
 * @param fragment - XML fragment to replace with
 * @returns Modified XML string
 * @throws Error if path is invalid or line number is out of bounds
 */
export function applyReplace(xml: string, path: string, fragment: string): string {
  const lineNum = parseLinePath(path);
  const lines = xml.split('\n');

  if (lineNum > lines.length) {
    throw new Error(`Line number ${lineNum} out of bounds (max: ${lines.length})`);
  }

  lines[lineNum - 1] = fragment;
  return lines.join('\n');
}

/**
 * Delete XML at specified path.
 * @param xml - Original XML string
 * @param path - Path to delete (format: "line:N")
 * @returns Modified XML string
 * @throws Error if path is invalid or line number is out of bounds
 */
export function applyDelete(xml: string, path: string): string {
  const lineNum = parseLinePath(path);
  const lines = xml.split('\n');

  if (lineNum > lines.length) {
    throw new Error(`Line number ${lineNum} out of bounds (max: ${lines.length})`);
  }

  lines.splice(lineNum - 1, 1);
  return lines.join('\n');
}
