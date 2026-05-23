# ZP Editor AI Panel Integration - Design Specification

**Date:** 2026-05-23  
**Author:** AI Assistant  
**Status:** Approved  
**Version:** 1.0

## Executive Summary

Integrate the existing AI panel (`ai-panel.js`) from `scheduler.html` into the ZP Flow Editor to enable AI-assisted editing of ZennoPoster XML templates. The AI agent will analyze XML structure, propose changes via diff modals, and allow users to accept/reject modifications before applying them to the visual graph.

**Key Features:**
- AI panel accessible via `[⟡ AI]` button in toolbar
- Agent receives full ZennoPoster XML context + metadata
- Agent proposes changes via `proposeZpXmlChange` tool
- User sees diff modal with Before/After comparison
- Changes applied to React state with visual highlighting
- No automatic file saves - user controls when to persist

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Components](#2-components)
3. [Data Flow](#3-data-flow)
4. [API Contracts](#4-api-contracts)
5. [Implementation Details](#5-implementation-details)
6. [Testing Strategy](#6-testing-strategy)
7. [Deployment](#7-deployment)

---

## 1. Architecture Overview

### 1.1. System Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Browser: http://localhost:10993/zp-editor/                 │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐ │
│  │  React App (ZP Editor)                                 │ │
│  │  ┌──────────────────────────────────────────────────┐  │ │
│  │  │  Toolbar Component                               │  │ │
│  │  │  [Load] [Save] [Undo] [Redo] [⟡ AI] ← NEW       │  │ │
│  │  └──────────────────────────────────────────────────┘  │ │
│  │  ┌──────────────────────────────────────────────────┐  │ │
│  │  │  Zustand Store (nodes, edges, history)           │  │ │
│  │  │  ↓ exported to                                   │  │ │
│  │  │  window.zpEditorStore                            │  │ │
│  │  └──────────────────────────────────────────────────┘  │ │
│  │  ┌──────────────────────────────────────────────────┐  │ │
│  │  │  window.ZpEditorAPI (new)                        │  │ │
│  │  │  • getContext()                                  │  │ │
│  │  │  • proposeXmlChange(proposal)                    │  │ │
│  │  │  • highlightNodes(ids, options)                  │  │ │
│  │  └──────────────────────────────────────────────────┘  │ │
│  └────────────────────────────────────────────────────────┘ │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐ │
│  │  ai-panel.js (injected via <script>)                  │ │
│  │  • Side panel UI (right side)                         │ │
│  │  • Chat interface                                     │ │
│  │  • Context provider: window.ZpEditorAPI.getContext()  │ │
│  └────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
                          ↕ HTTP
┌──────────────────────────────────────────────────────────────┐
│  z3nIO Server (ASP.NET Core)                                 │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Existing: /ai/chat (SSE streaming)                    │  │
│  │  • Receives: { chatId, message, cwd, model }           │  │
│  │  • Returns: SSE stream with deltas & tool calls        │  │
│  └────────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Existing: ZpEditorHandler                             │  │
│  │  • GET  /zp-editor/xml?path=...                        │  │
│  │  • POST /zp-editor/xml { path, xml }                   │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
                          ↕
┌──────────────────────────────────────────────────────────────┐
│  AI Agent (backend)                                          │
│  Tools available:                                            │
│  • proposeZpXmlChange(description, operation, ...) ← NEW     │
│  • Standard tools: read files, bash, etc.                    │
└──────────────────────────────────────────────────────────────┘
```

### 1.2. Design Principles

**Why:** Explains the rationale behind key architectural decisions.

1. **Minimal Integration** - Reuse existing `ai-panel.js` without modifications to reduce implementation time and maintain consistency with `scheduler.html`.

2. **User Control** - Agent proposes changes via diff modals; user explicitly accepts/rejects. No automatic file saves to prevent accidental data loss.

3. **ZennoPoster Awareness** - Agent understands ZP XML structure (Actions, Variables, Conditions) and generates valid fragments, not generic XML.

4. **Visual Feedback** - Changed nodes are highlighted with green pulse animation for 3 seconds after applying changes.

5. **Separation of Concerns** - `window.ZpEditorAPI` provides clean interface between vanilla JS (`ai-panel.js`) and React app.

**How to apply:** When extending functionality, maintain these principles. For example, new agent capabilities should still require user approval before modifying state.

---

## 2. Components

### 2.1. Frontend: window.ZpEditorAPI

**Purpose:** Bridge between vanilla JS (`ai-panel.js`) and React app. Provides clean API for AI agent to interact with editor state.

**Location:** `src/api/windowApi.ts` (new file)

**Interface:**
```typescript
interface ZpEditorAPI {
  version: string; // "1.0.0"
  
  getContext(): ZpEditorContext;
  proposeXmlChange(proposal: XmlChangeProposal): void;
  highlightNodes(nodeIds: string[], options?: HighlightOptions): void;
  getStore(): any;
}

interface ZpEditorContext {
  xml: string;              // Full ZennoPoster XML document
  filePath: string | null;  // File path or null if unsaved
  nodeCount: number;        // Number of Action nodes
  edgeCount: number;        // Number of connections
  selectedNodes: string[];  // IDs of selected nodes
  metadata: {
    modified: boolean;      // true if unsaved changes exist
    projectName: string;    // Project name from XML or "Untitled"
    variables: string[];    // List of variables ["url", "result", ...]
    historySize: number;    // Undo stack size
  };
}

interface XmlChangeProposal {
  description: string;      // "Add HTTP POST request after navigation"
  operation: 'insert' | 'replace' | 'delete';
  targetNodeId?: string;    // Target node ID for operation
  xmlFragment: string;      // Valid ZennoPoster XML fragment
}
```

**Responsibilities:**
- Serialize React state to ZennoPoster XML
- Parse XML to extract metadata (project name, variables)
- Dispatch custom events to trigger diff modal
- Apply CSS classes for node highlighting

### 2.2. Frontend: XmlDiffModal Component

**Purpose:** Show before/after comparison of XML changes and allow user to accept/reject.

**Location:** `src/components/XmlDiffModal.tsx` (new file)

**UI Layout:**
```
┌─────────────────────────────────────────────┐
│  AI Proposal: Add HTTP request action      │
├─────────────────────────────────────────────┤
│  ┌─────────────┬─────────────┐              │
│  │   Before    │    After    │              │
│  ├─────────────┼─────────────┤              │
│  │ <Action>    │ <Action>    │              │
│  │   ...       │   ...       │              │
│  │             │ + <Action   │ ← green      │
│  │             │     type=   │              │
│  │             │     "HTTP"> │              │
│  └─────────────┴─────────────┘              │
│                                             │
│  [Reject]  [Edit in Monaco]  [Apply]       │
└─────────────────────────────────────────────┘
```

**Responsibilities:**
- Listen for `zp-editor:show-diff` custom event
- Compute XML diff (highlight additions/deletions)
- Parse XML fragment and update Zustand store on Apply
- Trigger node highlighting after applying changes

### 2.3. Frontend: Toolbar Modification

**Purpose:** Add AI button to open panel.

**Location:** `src/components/Toolbar.tsx` (modify existing)

**Changes:**
```tsx
<button 
  className="toolbar-button ai-button"
  onClick={() => window.AiPanel?.toggle()}
  title="Open AI Assistant (Ctrl+K)"
>
  ⟡ AI
</button>
```

**Styles:**
```css
.ai-button {
  border-color: var(--accent);
  color: var(--accent);
  font-weight: 600;
}
```

### 2.4. Frontend: ai-panel.js Integration

**Purpose:** Reuse existing AI panel without modifications.

**Location:** `wwwroot/zp-editor/index.html` (modify)

**Integration:**
```html
<script src="/js/ai-panel.js"></script>
<script>
  window.addEventListener('DOMContentLoaded', () => {
    const checkApi = setInterval(() => {
      if (window.ZpEditorAPI && window.AiPanel) {
        clearInterval(checkApi);
        
        window.AiPanel.setContext(() => {
          const ctx = window.ZpEditorAPI.getContext();
          return `
<zp-editor-context>
  <file-path>${ctx.filePath || 'unsaved'}</file-path>
  <statistics>
    Project: "${ctx.metadata.projectName}"
    Actions: ${ctx.nodeCount}
    Variables: ${ctx.metadata.variables.join(', ')}
  </statistics>
  <zennoposter-xml>
${ctx.xml}
  </zennoposter-xml>
</zp-editor-context>
          `.trim();
        });
      }
    }, 100);
  });
</script>
```

### 2.5. Backend: proposeZpXmlChange Tool

**Purpose:** Allow AI agent to propose XML changes.

**Location:** `Handlers/AiHandler.cs` (modify existing)

**Tool Definition:**
```csharp
new ToolDefinition {
  Name = "proposeZpXmlChange",
  Description = "Propose a change to ZennoPoster XML template",
  InputSchema = new {
    type = "object",
    properties = new {
      description = new { type = "string" },
      operation = new { 
        type = "string", 
        @enum = new[] { "insert", "replace", "delete" } 
      },
      targetNodeId = new { type = "string" },
      xmlFragment = new { type = "string" }
    },
    required = new[] { "description", "operation" }
  }
}
```

**Execution:**
Returns JS code that calls `window.ZpEditorAPI.proposeXmlChange(...)`.

### 2.6. Backend: System Prompt Enhancement

**Purpose:** Teach agent about ZennoPoster XML structure.

**Location:** `Handlers/AiHandler.cs` (modify system prompt)

**Content:** Includes ZP XML structure documentation, common action types (Navigate, HTTP, GetText, Click, Condition, Loop), variable syntax, and usage examples.

---

## 3. Data Flow

### 3.1. Complete User Journey

**Scenario:** User asks "Добавь HTTP POST запрос после первого действия"

#### Step 1: User Opens AI Panel
```
User clicks [⟡ AI] button
  ↓
AiPanel.open() called
  ↓
ai-panel.js calls window.ZpEditorAPI.getContext()
  ↓
Context serialized to XML and added to first message:

<context>
  <zp-editor-context>
    <file-path>W:\work_hard\zenoposter\parser.xml</file-path>
    <statistics>
      Project: "Web Parser"
      Actions: 15
      Variables: url, result
    </statistics>
    <zennoposter-xml>
      <?xml version="1.0"?>
      <Project>
        <Actions>
          <Action type="Navigate" id="act-1">...</Action>
          ...
        </Actions>
      </Project>
    </zennoposter-xml>
  </zp-editor-context>
</context>

Добавь HTTP POST запрос после первого действия
```

#### Step 2: Request Sent to Backend
```
POST /ai/chat
{
  "chatId": "chat-abc123",
  "message": "<context>...</context>\n\nДобавь HTTP POST запрос...",
  "model": "kr/claude-sonnet-4.5"
}
  ↓
AI Agent analyzes ZP XML structure
  ↓
Agent decides to use proposeZpXmlChange tool
```

#### Step 3: Agent Calls Tool
```
SSE stream:

event: tool
data: {
  "name": "proposeZpXmlChange",
  "input": {
    "description": "Добавить HTTP POST запрос после навигации",
    "operation": "insert",
    "targetNodeId": "act-1",
    "xmlFragment": "<Action type=\"HTTP\" id=\"act-http-1\">...</Action>"
  }
}
  ↓
Backend executes tool → returns JS code:

event: tool_result
data: {
  "content": "window.ZpEditorAPI.proposeXmlChange({...})"
}
```

#### Step 4: Agent Sends Response
```
event: delta
data: { "text": "Я создал HTTP POST запрос. Выполните:\n\n```js\n" }

event: delta
data: { "text": "window.ZpEditorAPI.proposeXmlChange({...})\n```" }

event: done
  ↓
ai-panel.js renders message with [Execute] button
```

#### Step 5: User Applies Changes
```
User clicks [Execute]
  ↓
eval(jsCode) executes
  ↓
window.ZpEditorAPI.proposeXmlChange() called
  ↓
Custom event dispatched: 'zp-editor:show-diff'
  ↓
XmlDiffModal opens with before/after comparison
  ↓
User clicks [Apply Changes]
  ↓
Zustand store updated with new node
  ↓
React Flow re-renders graph
  ↓
New node highlighted green (3 sec pulse)
  ↓
Modal closes
  ↓
AI panel shows: "✓ Changes applied successfully"
```

#### Step 6: User Saves (Optional)
```
User clicks [Save] in Toolbar
  ↓
POST /zp-editor/xml { path, xml }
  ↓
File saved to disk
  ↓
UI shows: "Saved to parser.xml"
```

### 3.2. Error Handling

**Invalid XML Fragment:**
- Agent generates invalid XML
- XmlDiffModal shows parse error
- User can click [Edit in Monaco] to fix manually

**Tool Execution Failure:**
- Backend returns error in tool_result
- Agent explains error to user
- User can retry with corrected input

**Network Failure:**
- SSE stream interrupted
- ai-panel.js shows connection error
- User can retry message

---

## 4. API Contracts

### 4.1. window.ZpEditorAPI

**Full TypeScript Interface:**

```typescript
interface ZpEditorAPI {
  version: string; // "1.0.0"
  
  getContext(): ZpEditorContext;
  proposeXmlChange(proposal: XmlChangeProposal): void;
  highlightNodes(nodeIds: string[], options?: HighlightOptions): void;
  getStore(): any;
}

interface ZpEditorContext {
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

interface XmlChangeProposal {
  description: string;
  operation: 'insert' | 'replace' | 'delete';
  targetNodeId?: string;
  xmlFragment: string;
}

interface HighlightOptions {
  duration?: number;  // Default: 3000ms
  color?: string;     // Default: "#3fb950"
  pulse?: boolean;    // Default: true
}
```

**Usage Examples:**

```javascript
// Get context
const ctx = window.ZpEditorAPI.getContext();
console.log(ctx.nodeCount); // 15

// Propose change
window.ZpEditorAPI.proposeXmlChange({
  description: "Добавить HTTP запрос",
  operation: "insert",
  targetNodeId: "act-1",
  xmlFragment: `<Action type="HTTP" id="act-http-1">
  <Method>POST</Method>
  <Url>https://api.example.com</Url>
</Action>`
});

// Highlight nodes
window.ZpEditorAPI.highlightNodes(["act-1", "act-2"], {
  duration: 5000,
  color: "#58a6ff"
});
```

### 4.2. Backend Tool: proposeZpXmlChange

**Tool Schema:**

```json
{
  "name": "proposeZpXmlChange",
  "description": "Propose a change to the ZennoPoster XML template. Shows a diff modal to the user who can accept or reject the change.",
  "input_schema": {
    "type": "object",
    "properties": {
      "description": {
        "type": "string",
        "description": "Human-readable description in Russian. Example: 'Добавить HTTP запрос к API'"
      },
      "operation": {
        "type": "string",
        "enum": ["insert", "replace", "delete"],
        "description": "insert: add new node, replace: modify existing, delete: remove node"
      },
      "targetNodeId": {
        "type": "string",
        "description": "For insert: insert after this node. For replace/delete: the node to modify/remove."
      },
      "xmlFragment": {
        "type": "string",
        "description": "Valid ZennoPoster XML fragment. Complete <Action> element. Not needed for delete."
      }
    },
    "required": ["description", "operation"]
  }
}
```

**Tool Response:**

```json
{
  "success": true,
  "jsCode": "window.ZpEditorAPI.proposeXmlChange({...})",
  "message": "Proposal created. User will see a diff modal."
}
```

### 4.3. Backend Endpoints (Existing)

**POST /ai/chat**

Request:
```json
{
  "chatId": "chat-abc123",
  "message": "<context>...</context>\n\nUser message",
  "cwd": "",
  "model": "kr/claude-sonnet-4.5"
}
```

Response (SSE):
```
event: delta
data: {"text": "..."}

event: tool
data: {"name": "proposeZpXmlChange", "input": {...}}

event: tool_result
data: {"tool_use_id": "toolu_123", "content": "..."}

event: done
data: {"stop_reason": "end_turn"}
```

**GET /zp-editor/xml?path=...**

Response:
```json
{
  "path": "W:\\work_hard\\zenoposter\\parser.xml",
  "xml": "<?xml version=\"1.0\"?>..."
}
```

**POST /zp-editor/xml**

Request:
```json
{
  "path": "W:\\work_hard\\zenoposter\\parser.xml",
  "xml": "<?xml version=\"1.0\"?>..."
}
```

Response:
```json
{
  "ok": true,
  "path": "W:\\work_hard\\zenoposter\\parser.xml"
}
```

---

## 5. Implementation Details

### 5.1. File Structure

```
src/
├── api/
│   ├── windowApi.ts          ← NEW: window.ZpEditorAPI implementation
│   └── zpServerClient.ts     ← Existing
├── components/
│   ├── Toolbar.tsx           ← MODIFY: add [⟡ AI] button
│   ├── XmlDiffModal.tsx      ← NEW: diff viewer modal
│   └── ExecuteButton.tsx     ← NEW: Execute button for AI chat
├── store/
│   └── useStore.ts           ← MODIFY: export to window.zpEditorStore
├── utils/
│   ├── xmlParser.ts          ← NEW: ZP XML parsing/serialization
│   ├── xmlDiff.ts            ← NEW: compute XML diff
│   └── xmlOperations.ts      ← NEW: apply XML changes
└── App.tsx                   ← MODIFY: initialize window API

wwwroot/zp-editor/
├── index.html                ← MODIFY: add ai-panel.js script
└── assets/                   ← Existing build files

Handlers/
└── AiHandler.cs              ← MODIFY: add proposeZpXmlChange tool
```

### 5.2. Frontend Implementation

#### windowApi.ts (Core Implementation)

```typescript
// src/api/windowApi.ts
import { useStore } from '../store/useStore';
import { serializeZpXml, parseZpXml } from '../utils/xmlParser';

class ZpEditorAPIImpl implements ZpEditorAPI {
  version = '1.0.0';

  getContext(): ZpEditorContext {
    const state = useStore.getState();
    const xml = serializeZpXml(state.nodes, state.edges);
    const parsed = parseZpXml(xml);
    
    return {
      xml,
      filePath: state.currentFilePath || null,
      nodeCount: state.nodes.length,
      edgeCount: state.edges.length,
      selectedNodes: state.nodes.filter(n => n.selected).map(n => n.id),
      metadata: {
        modified: state.isModified,
        projectName: parsed.projectName || 'Untitled',
        variables: parsed.variables || [],
        historySize: state.history.length,
      },
    };
  }

  proposeXmlChange(proposal: XmlChangeProposal): void {
    window.dispatchEvent(new CustomEvent('zp-editor:show-diff', {
      detail: proposal
    }));
  }

  highlightNodes(nodeIds: string[], options?: HighlightOptions): void {
    const duration = options?.duration || 3000;
    const color = options?.color || '#3fb950';
    const pulse = options?.pulse !== false;

    nodeIds.forEach(id => {
      const node = document.querySelector(`[data-id="${id}"]`);
      if (node) {
        node.classList.add('ai-highlighted');
        node.style.setProperty('--highlight-color', color);
        if (pulse) node.classList.add('ai-pulse');
      }
    });

    setTimeout(() => {
      nodeIds.forEach(id => {
        const node = document.querySelector(`[data-id="${id}"]`);
        if (node) {
          node.classList.remove('ai-highlighted', 'ai-pulse');
        }
      });
    }, duration);
  }

  getStore(): any {
    return useStore;
  }
}

export const zpEditorAPI = new ZpEditorAPIImpl();

export function initWindowAPI() {
  window.ZpEditorAPI = zpEditorAPI;
  window.zpEditorStore = useStore;
  console.log('[ZpEditor] Window API initialized');
}
```

#### XmlDiffModal.tsx

```tsx
// src/components/XmlDiffModal.tsx
import React, { useState, useEffect } from 'react';
import { XmlChangeProposal } from '../api/windowApi';
import { useStore } from '../store/useStore';
import { applyXmlChange } from '../utils/xmlOperations';
import { computeXmlDiff } from '../utils/xmlDiff';
import { serializeZpXml, parseZpXml } from '../utils/xmlParser';

export const XmlDiffModal: React.FC = () => {
  const [proposal, setProposal] = useState<XmlChangeProposal | null>(null);
  const [diff, setDiff] = useState<string>('');
  const { nodes, edges } = useStore();

  useEffect(() => {
    const handler = (e: CustomEvent) => {
      const p = e.detail as XmlChangeProposal;
      setProposal(p);
      
      const currentXml = serializeZpXml(nodes, edges);
      const newXml = applyXmlChange(currentXml, p);
      const diffHtml = computeXmlDiff(currentXml, newXml);
      setDiff(diffHtml);
    };

    window.addEventListener('zp-editor:show-diff', handler as any);
    return () => window.removeEventListener('zp-editor:show-diff', handler as any);
  }, [nodes, edges]);

  const handleApply = () => {
    if (!proposal) return;

    const result = applyXmlChange(serializeZpXml(nodes, edges), proposal);
    const parsed = parseZpXml(result);
    
    useStore.setState({
      nodes: parsed.nodes,
      edges: parsed.edges,
      isModified: true,
    });

    if (proposal.targetNodeId) {
      window.ZpEditorAPI.highlightNodes([proposal.targetNodeId]);
    }

    setProposal(null);
  };

  const handleReject = () => setProposal(null);

  if (!proposal) return null;

  return (
    <div className="modal-overlay" onClick={handleReject}>
      <div className="modal-content" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <h3>AI Proposal</h3>
          <button onClick={handleReject}>✕</button>
        </div>
        
        <div className="modal-body">
          <p className="proposal-description">{proposal.description}</p>
          <div className="diff-viewer" dangerouslySetInnerHTML={{ __html: diff }} />
        </div>
        
        <div className="modal-footer">
          <button className="btn" onClick={handleReject}>Reject</button>
          <button className="btn primary" onClick={handleApply}>Apply Changes</button>
        </div>
      </div>
    </div>
  );
};
```

#### index.html Modification

```html
<!DOCTYPE html>
<html lang="ru">
  <head>
    <meta charset="UTF-8" />
    <title>ZP Flow Editor</title>
    <script type="module" crossorigin src="./assets/index-CYuTb1-1.js"></script>
    <link rel="stylesheet" crossorigin href="./assets/index-NONk98Em.css">
    
    <!-- AI Panel Integration -->
    <script src="/js/ai-panel.js"></script>
    <style>
      .ai-highlighted {
        outline: 3px solid var(--highlight-color, #3fb950) !important;
        outline-offset: 2px;
      }
      .ai-pulse {
        animation: ai-pulse 1s ease-in-out infinite;
      }
      @keyframes ai-pulse {
        0%, 100% { outline-opacity: 1; }
        50% { outline-opacity: 0.4; }
      }
    </style>
  </head>
  <body>
    <div id="root"></div>
    
    <script>
      window.addEventListener('DOMContentLoaded', () => {
        const checkApi = setInterval(() => {
          if (window.ZpEditorAPI && window.AiPanel) {
            clearInterval(checkApi);
            
            window.AiPanel.setContext(() => {
              const ctx = window.ZpEditorAPI.getContext();
              return `
<zp-editor-context>
  <file-path>${ctx.filePath || 'unsaved'}</file-path>
  <statistics>
    Project: "${ctx.metadata.projectName}"
    Actions: ${ctx.nodeCount}
    Variables: ${ctx.metadata.variables.join(', ')}
  </statistics>
  <zennoposter-xml>
${ctx.xml}
  </zennoposter-xml>
</zp-editor-context>
              `.trim();
            });
            
            console.log('[ZpEditor] AI Panel configured');
          }
        }, 100);
      });
    </script>
  </body>
</html>
```

### 5.3. Backend Implementation

#### Tool Registration (AiHandler.cs)

```csharp
// Handlers/AiHandler.cs
private static readonly List<ToolDefinition> _tools = new()
{
    // Existing tools...
    
    new ToolDefinition
    {
        Name = "proposeZpXmlChange",
        Description = "Propose a change to the ZennoPoster XML template. Shows a diff modal to the user.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                description = new
                {
                    type = "string",
                    description = "Human-readable description in Russian"
                },
                operation = new
                {
                    type = "string",
                    @enum = new[] { "insert", "replace", "delete" }
                },
                targetNodeId = new { type = "string" },
                xmlFragment = new { type = "string" }
            },
            required = new[] { "description", "operation" }
        }
    }
};
```

#### Tool Execution (AiHandler.cs)

```csharp
private string ExecuteProposeZpXmlChange(JsonElement input)
{
    try
    {
        var description = input.GetProperty("description").GetString() ?? "";
        var operation = input.GetProperty("operation").GetString() ?? "insert";
        var targetNodeId = input.TryGetProperty("targetNodeId", out var tid) 
            ? tid.GetString() 
            : null;
        var xmlFragment = input.TryGetProperty("xmlFragment", out var xf) 
            ? xf.GetString() 
            : "";

        var escapedDescription = EscapeJs(description);
        var escapedXmlFragment = EscapeJs(xmlFragment);
        var escapedTargetNodeId = targetNodeId != null 
            ? $"'{EscapeJs(targetNodeId)}'" 
            : "null";

        var jsCode = $@"
window.ZpEditorAPI.proposeXmlChange({{
  description: '{escapedDescription}',
  operation: '{operation}',
  targetNodeId: {escapedTargetNodeId},
  xmlFragment: `{escapedXmlFragment}`
}});
        ".Trim();

        return JsonSerializer.Serialize(new
        {
            success = true,
            jsCode,
            message = "Proposal created. User will see a diff modal."
        });
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { success = false, error = ex.Message });
    }
}

private static string EscapeJs(string str)
{
    if (string.IsNullOrEmpty(str)) return "";
    
    return str
        .Replace("\\", "\\\\")
        .Replace("'", "\\'")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("`", "\\`");
}
```

---


## 6. Testing Strategy

### 6.1. Unit Tests

**Frontend (Jest + React Testing Library):**

```typescript
// src/api/windowApi.test.ts
describe('ZpEditorAPI', () => {
  test('getContext returns valid structure', () => {
    const ctx = window.ZpEditorAPI.getContext();
    expect(ctx).toHaveProperty('xml');
    expect(ctx).toHaveProperty('nodeCount');
    expect(ctx.metadata).toHaveProperty('projectName');
  });

  test('proposeXmlChange dispatches event', () => {
    const handler = jest.fn();
    window.addEventListener('zp-editor:show-diff', handler);
    
    window.ZpEditorAPI.proposeXmlChange({
      description: 'Test',
      operation: 'insert',
      xmlFragment: '<Action />'
    });
    
    expect(handler).toHaveBeenCalled();
  });
});
```

**Backend (xUnit):**

```csharp
// Tests/AiHandlerTests.cs
[Fact]
public void ExecuteProposeZpXmlChange_ValidInput_ReturnsJsCode()
{
    var input = JsonDocument.Parse(@"{
        ""description"": ""Test"",
        ""operation"": ""insert"",
        ""xmlFragment"": ""<Action />""
    }").RootElement;
    
    var result = handler.ExecuteProposeZpXmlChange(input);
    var json = JsonSerializer.Deserialize<JsonElement>(result);
    
    Assert.True(json.GetProperty("success").GetBoolean());
    Assert.Contains("window.ZpEditorAPI", json.GetProperty("jsCode").GetString());
}
```

### 6.2. Integration Tests

**Test Scenarios:**

1. **AI Panel Opens Successfully**
   - Click [⟡ AI] button
   - Verify panel slides in from right
   - Verify context is sent with first message

2. **Agent Proposes Valid Change**
   - Send message: "Добавь HTTP запрос"
   - Verify tool call appears in stream
   - Verify JS code returned
   - Click [Execute]
   - Verify diff modal opens

3. **User Accepts Change**
   - Apply change in modal
   - Verify new node appears in graph
   - Verify node is highlighted green
   - Verify undo history updated

4. **User Rejects Change**
   - Reject change in modal
   - Verify graph unchanged
   - Verify modal closes

5. **Invalid XML Handling**
   - Agent generates malformed XML
   - Verify parse error shown
   - Verify user can edit manually

### 6.3. Manual Testing Checklist

- [ ] AI button visible in toolbar
- [ ] AI panel opens/closes correctly
- [ ] Panel width resizable
- [ ] Context includes full XML
- [ ] Agent understands ZP structure
- [ ] Tool calls execute successfully
- [ ] Diff modal shows correct changes
- [ ] Apply updates graph correctly
- [ ] Nodes highlight after apply
- [ ] Undo/redo works with AI changes
- [ ] Save persists AI-modified XML
- [ ] Multiple changes in sequence work
- [ ] Panel works with unsaved files
- [ ] Panel works with loaded files

---

## 7. Deployment

### 7.1. Build Process

**Frontend (React App):**

```bash
cd z3n8/zp-flow-editor
npm run build
cp -r dist/* ../z3nIO/wwwroot/zp-editor/
```

**Backend (z3nIO):**

```bash
cd z3nIO
dotnet build
dotnet run
```

### 7.2. Deployment Checklist

**Pre-deployment:**
- [ ] All unit tests pass
- [ ] Integration tests pass
- [ ] Manual testing completed
- [ ] Code reviewed
- [ ] Documentation updated

**Deployment Steps:**

1. **Build React app** with new components
2. **Copy build** to `wwwroot/zp-editor/`
3. **Verify** `ai-panel.js` accessible at `/js/ai-panel.js`
4. **Restart** z3nIO server
5. **Test** at `http://localhost:10993/zp-editor/`

**Post-deployment Verification:**

- [ ] AI button appears in toolbar
- [ ] Clicking button opens panel
- [ ] Context sent correctly
- [ ] Agent can propose changes
- [ ] Diff modal works
- [ ] Changes apply correctly
- [ ] No console errors

### 7.3. Rollback Plan

If issues occur:

1. **Revert frontend build:**
   ```bash
   git checkout HEAD -- wwwroot/zp-editor/
   ```

2. **Revert backend changes:**
   ```bash
   git checkout HEAD -- Handlers/AiHandler.cs
   ```

3. **Rebuild and restart:**
   ```bash
   dotnet build
   dotnet run
   ```

### 7.4. Monitoring

**Metrics to Track:**

- AI panel open rate
- Tool call success rate
- Diff modal accept/reject ratio
- Average changes per session
- Error rates (parse errors, tool failures)

**Logs to Monitor:**

- `[ZpEditor] Window API initialized` - confirms API loaded
- `[ZpEditor] AI Panel configured` - confirms integration
- Tool execution errors in backend logs
- XML parse errors in browser console

---

## 8. Future Enhancements

### 8.1. Short-term (Next Sprint)

1. **Monaco Editor Integration** - Allow editing XML in diff modal before applying
2. **Change History** - Show list of AI-proposed changes in session
3. **Keyboard Shortcuts** - Ctrl+K to open AI panel, Ctrl+Enter to execute code
4. **Better Error Messages** - User-friendly explanations for XML parse errors

### 8.2. Long-term

1. **Real-time Collaboration** - Multiple users editing same template with AI
2. **Change Templates** - Save common AI change patterns for reuse
3. **Validation** - Validate ZP XML against schema before applying
4. **AI Suggestions** - Proactive suggestions based on template analysis
5. **Export/Import** - Export AI conversation with changes for documentation

---

## 9. Appendix

### 9.1. ZennoPoster XML Reference

**Common Action Types:**

```xml
<!-- Navigate -->
<Action type="Navigate" id="act-1">
  <Url>https://example.com</Url>
</Action>

<!-- HTTP Request -->
<Action type="HTTP" id="act-2">
  <Method>POST</Method>
  <Url>https://api.example.com</Url>
  <Headers>
    <Header name="Content-Type">application/json</Header>
  </Headers>
  <Body>{"key": "value"}</Body>
  <ResultVariable>response</ResultVariable>
</Action>

<!-- Get Text -->
<Action type="GetText" id="act-3">
  <Selector>.title</Selector>
  <Variable>pageTitle</Variable>
</Action>

<!-- Click -->
<Action type="Click" id="act-4">
  <Selector>#submit-button</Selector>
</Action>

<!-- Condition -->
<Action type="Condition" id="act-5">
  <Expression>{-Variable.result-} != ""</Expression>
  <TrueAction>act-6</TrueAction>
  <FalseAction>act-7</FalseAction>
</Action>

<!-- Loop -->
<Action type="Loop" id="act-6">
  <Count>10</Count>
  <BodyAction>act-7</BodyAction>
</Action>
```

### 9.2. Glossary

- **ZennoPoster (ZP)** - Automation platform for web scraping and browser automation
- **ZP XML** - XML format used by ZennoPoster to define automation workflows
- **Action** - Single step in ZP workflow (Navigate, HTTP, Click, etc.)
- **Variable** - Named value that can be used across actions
- **Diff Modal** - UI component showing before/after comparison of changes
- **Zustand** - State management library used in React app
- **SSE** - Server-Sent Events, used for streaming AI responses

---

**End of Specification**
