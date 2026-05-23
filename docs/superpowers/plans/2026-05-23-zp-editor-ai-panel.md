# ZP Editor AI Panel Integration - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate ai-panel.js into ZP Flow Editor to enable AI-assisted editing of ZennoPoster XML templates with diff-based change proposals.

**Architecture:** Reuse existing ai-panel.js without modifications. Create window.ZpEditorAPI bridge between vanilla JS and React. Agent proposes XML changes via proposeZpXmlChange tool, user reviews in diff modal before applying to Zustand store.

**Tech Stack:** React, TypeScript, Zustand, ai-panel.js (vanilla JS), ASP.NET Core, C#

---

## File Structure

### Frontend (React App - z3n8/zp-flow-editor)

**New Files:**
- `src/api/windowApi.ts` - window.ZpEditorAPI implementation
- `src/components/XmlDiffModal.tsx` - Diff viewer modal component
- `src/utils/xmlParser.ts` - ZP XML serialization/parsing
- `src/utils/xmlDiff.ts` - Compute XML diff for display
- `src/utils/xmlOperations.ts` - Apply XML changes to state

**Modified Files:**
- `src/components/Toolbar.tsx` - Add [⟡ AI] button
- `src/store/useStore.ts` - Export to window.zpEditorStore
- `src/App.tsx` - Initialize window API
- `wwwroot/zp-editor/index.html` - Add ai-panel.js script

### Backend (z3nIO)

**Modified Files:**
- `Handlers/AiHandler.cs` - Add proposeZpXmlChange tool + system prompt

---

## Task 1: XML Parser Utilities

**Files:**
- Create: `src/utils/xmlParser.ts`

- [ ] **Step 1: Create xmlParser.ts with type definitions**

```typescript
// src/utils/xmlParser.ts

export interface ZpProject {
  projectName: string;
  variables: string[];
  actions: ZpAction[];
}

export interface ZpAction {
  id: string;
  type: string;
  properties: Record<string, any>;
}

export function parseZpXml(xml: string): ZpProject {
  // Placeholder - will implement in next step
  return { projectName: '', variables: [], actions: [] };
}

export function serializeZpXml(nodes: any[], edges: any[]): string {
  // Placeholder - will implement in next step
  return '';
}
```

- [ ] **Step 2: Implement parseZpXml function**

```typescript
export function parseZpXml(xml: string): ZpProject {
  const parser = new DOMParser();
  const doc = parser.parseFromString(xml, 'text/xml');
  
  // Extract project name
  const projectName = doc.querySelector('Project')?.getAttribute('name') || 'Untitled';
  
  // Extract variables
  const variables: string[] = [];
  doc.querySelectorAll('Variables > Variable').forEach(v => {
    const name = v.getAttribute('name');
    if (name) variables.push(name);
  });
  
  // Extract actions
  const actions: ZpAction[] = [];
  doc.querySelectorAll('Actions > Action').forEach(a => {
    const id = a.getAttribute('id') || '';
    const type = a.getAttribute('type') || '';
    const properties: Record<string, any> = {};
    
    a.childNodes.forEach(child => {
      if (child.nodeType === Node.ELEMENT_NODE) {
        const el = child as Element;
        properties[el.tagName] = el.textContent || '';
      }
    });
    
    actions.push({ id, type, properties });
  });
  
  return { projectName, variables, actions };
}
```

- [ ] **Step 3: Implement serializeZpXml function**

```typescript
export function serializeZpXml(nodes: any[], edges: any[]): string {
  const variables = nodes
    .filter(n => n.data?.isVariable)
    .map(n => `    <Variable name="${n.data.name}" type="string" />`)
    .join('\n');
  
  const actions = nodes
    .filter(n => n.type === 'action')
    .map(n => {
      const props = Object.entries(n.data.properties || {})
        .map(([key, val]) => `    <${key}>${val}</${key}>`)
        .join('\n');
      
      return `    <Action type="${n.data.type}" id="${n.id}">\n${props}\n    </Action>`;
    })
    .join('\n');
  
  return `<?xml version="1.0" encoding="utf-8"?>
<Project name="${nodes[0]?.data?.projectName || 'Untitled'}">
  <Variables>
${variables}
  </Variables>
  <Actions>
${actions}
  </Actions>
</Project>`;
}
```

- [ ] **Step 4: Commit**

```bash
git add src/utils/xmlParser.ts
git commit -m "feat: add ZP XML parser utilities

- parseZpXml: extract project name, variables, actions
- serializeZpXml: convert React state to ZP XML format"
```

---

## Task 2: XML Diff Utilities

**Files:**
- Create: `src/utils/xmlDiff.ts`

- [ ] **Step 1: Create xmlDiff.ts with diff computation**

```typescript
// src/utils/xmlDiff.ts

export function computeXmlDiff(oldXml: string, newXml: string): string {
  const oldLines = oldXml.split('\n');
  const newLines = newXml.split('\n');
  
  let html = '<div class="diff-container">';
  html += '<div class="diff-column"><h4>Before</h4><pre>';
  
  // Simple line-by-line diff
  const maxLines = Math.max(oldLines.length, newLines.length);
  
  for (let i = 0; i < maxLines; i++) {
    const oldLine = oldLines[i] || '';
    const newLine = newLines[i] || '';
    
    if (oldLine === newLine) {
      html += `<div class="diff-line">${escapeHtml(oldLine)}</div>`;
    } else if (!newLine) {
      html += `<div class="diff-line diff-removed">${escapeHtml(oldLine)}</div>`;
    } else if (!oldLine) {
      html += `<div class="diff-line diff-added">${escapeHtml(newLine)}</div>`;
    } else {
      html += `<div class="diff-line diff-modified">${escapeHtml(oldLine)}</div>`;
    }
  }
  
  html += '</pre></div>';
  html += '<div class="diff-column"><h4>After</h4><pre>';
  
  for (let i = 0; i < maxLines; i++) {
    const oldLine = oldLines[i] || '';
    const newLine = newLines[i] || '';
    
    if (oldLine === newLine) {
      html += `<div class="diff-line">${escapeHtml(newLine)}</div>`;
    } else if (!oldLine) {
      html += `<div class="diff-line diff-added">${escapeHtml(newLine)}</div>`;
    } else if (!newLine) {
      html += `<div class="diff-line diff-removed"></div>`;
    } else {
      html += `<div class="diff-line diff-modified">${escapeHtml(newLine)}</div>`;
    }
  }
  
  html += '</pre></div></div>';
  return html;
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
```

- [ ] **Step 2: Add CSS styles for diff display**

```typescript
// Add to the same file
export const diffStyles = `
.diff-container {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
  font-family: 'Consolas', monospace;
  font-size: 11px;
}

.diff-column h4 {
  margin: 0 0 8px 0;
  font-size: 12px;
  color: var(--text2);
}

.diff-column pre {
  margin: 0;
  padding: 10px;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 4px;
  overflow-x: auto;
}

.diff-line {
  line-height: 1.5;
  padding: 2px 4px;
}

.diff-added {
  background: rgba(63, 185, 80, 0.15);
  color: var(--green, #3fb950);
}

.diff-removed {
  background: rgba(248, 81, 73, 0.15);
  color: var(--red, #f85149);
  text-decoration: line-through;
}

.diff-modified {
  background: rgba(210, 153, 34, 0.15);
  color: var(--yellow, #d29922);
}
`;
```

- [ ] **Step 3: Commit**

```bash
git add src/utils/xmlDiff.ts
git commit -m "feat: add XML diff computation utility

- computeXmlDiff: generate HTML diff view
- diffStyles: CSS for diff highlighting"
```

---

## Task 3: XML Operations Utilities

**Files:**
- Create: `src/utils/xmlOperations.ts`

- [ ] **Step 1: Create xmlOperations.ts with applyXmlChange**

```typescript
// src/utils/xmlOperations.ts
import { XmlChangeProposal } from '../api/windowApi';

export function applyXmlChange(currentXml: string, proposal: XmlChangeProposal): string {
  const parser = new DOMParser();
  const doc = parser.parseFromString(currentXml, 'text/xml');
  
  if (proposal.operation === 'insert') {
    return applyInsert(doc, proposal);
  } else if (proposal.operation === 'replace') {
    return applyReplace(doc, proposal);
  } else if (proposal.operation === 'delete') {
    return applyDelete(doc, proposal);
  }
  
  return currentXml;
}

function applyInsert(doc: Document, proposal: XmlChangeProposal): string {
  const targetNode = doc.querySelector(`Action[id="${proposal.targetNodeId}"]`);
  if (!targetNode) {
    // Append to end if target not found
    const actionsNode = doc.querySelector('Actions');
    if (actionsNode) {
      const fragmentDoc = new DOMParser().parseFromString(proposal.xmlFragment, 'text/xml');
      const newNode = doc.importNode(fragmentDoc.documentElement, true);
      actionsNode.appendChild(newNode);
    }
  } else {
    // Insert after target
    const fragmentDoc = new DOMParser().parseFromString(proposal.xmlFragment, 'text/xml');
    const newNode = doc.importNode(fragmentDoc.documentElement, true);
    targetNode.parentNode?.insertBefore(newNode, targetNode.nextSibling);
  }
  
  return new XMLSerializer().serializeToString(doc);
}

function applyReplace(doc: Document, proposal: XmlChangeProposal): string {
  const targetNode = doc.querySelector(`Action[id="${proposal.targetNodeId}"]`);
  if (targetNode) {
    const fragmentDoc = new DOMParser().parseFromString(proposal.xmlFragment, 'text/xml');
    const newNode = doc.importNode(fragmentDoc.documentElement, true);
    targetNode.parentNode?.replaceChild(newNode, targetNode);
  }
  
  return new XMLSerializer().serializeToString(doc);
}

function applyDelete(doc: Document, proposal: XmlChangeProposal): string {
  const targetNode = doc.querySelector(`Action[id="${proposal.targetNodeId}"]`);
  if (targetNode) {
    targetNode.parentNode?.removeChild(targetNode);
  }
  
  return new XMLSerializer().serializeToString(doc);
}
```

- [ ] **Step 2: Commit**

```bash
git add src/utils/xmlOperations.ts
git commit -m "feat: add XML operations utility

- applyXmlChange: apply insert/replace/delete operations
- Handles target node lookup and DOM manipulation"
```

---

## Task 4: Window API Implementation

**Files:**
- Create: `src/api/windowApi.ts`

- [ ] **Step 1: Create windowApi.ts with type definitions**

```typescript
// src/api/windowApi.ts

export interface ZpEditorAPI {
  version: string;
  getContext(): ZpEditorContext;
  proposeXmlChange(proposal: XmlChangeProposal): void;
  highlightNodes(nodeIds: string[], options?: HighlightOptions): void;
  getStore(): any;
}

export interface ZpEditorContext {
  xml: string;
  filePath: string | null;
  nodeCount: number;
  edgeCount: number;
  selectedNodes: string[];
  metadata: {
    modified: boolean;
    projectName: string;
    variables: string[];
    historySize: number;
  };
}

export interface XmlChangeProposal {
  description: string;
  operation: 'insert' | 'replace' | 'delete';
  targetNodeId?: string;
  xmlFragment: string;
}

export interface HighlightOptions {
  duration?: number;
  color?: string;
  pulse?: boolean;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/api/windowApi.ts
git commit -m "feat: add window API types"
```

---

## Task 5: Toolbar AI Button

**Files:**
- Modify: src/components/Toolbar.tsx

- [ ] Step 1: Add AI button
- [ ] Step 2: Commit

---

## Task 6: Backend Tool

**Files:**
- Modify: Handlers/AiHandler.cs

- [ ] Step 1: Add proposeZpXmlChange tool
- [ ] Step 2: Commit

---

## Task 7: Integration

- [ ] Step 1: Build and deploy React app
- [ ] Step 2: Test integration
- [ ] Step 3: Final commit

---

End of Implementation Plan
