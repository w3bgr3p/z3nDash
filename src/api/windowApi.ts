/**
 * Window API for AI Panel integration.
 * Exposes React functionality to vanilla JS code (ai-panel.js).
 */

import { useGraphStore } from '../store/graphStore';
import { generateZenPosterXML, reactFlowToZPGraph } from '../utils/xmlGenerator';

export interface ZpEditorAPI {
  version: string;
  getContext(): ZpEditorContext;
  proposeXmlChange(oldXml: string, newXml: string): void;
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
    historySize: number;
  };
}

export interface HighlightOptions {
  duration?: number;
  color?: string;
  pulse?: boolean;
}

/**
 * Implementation of ZpEditorAPI.
 */
class ZpEditorAPIImpl implements ZpEditorAPI {
  version = '1.0.0';

  /**
   * Get current editor state as context for AI.
   * @returns Editor context with XML and metadata
   */
  getContext(): ZpEditorContext {
    const state = useGraphStore.getState();

    // Convert React Flow graph to ZP XML
    const zpGraph = reactFlowToZPGraph({
      nodes: state.nodes,
      edges: state.edges
    });
    const xml = generateZenPosterXML(zpGraph);

    // Get selected nodes
    const selectedNodes = state.nodes
      .filter(n => n.id === state.selectedNodeId)
      .map(n => n.id);

    return {
      xml,
      filePath: null, // TODO: Add file path tracking to store
      nodeCount: state.nodes.length,
      edgeCount: state.edges.length,
      selectedNodes,
      metadata: {
        modified: false, // TODO: Add modified flag to store
        projectName: 'Untitled', // TODO: Extract from XML or store
        historySize: 0 // TODO: Get from historyStore
      }
    };
  }

  /**
   * Propose XML change with diff modal for user approval.
   * Dispatches custom event that XmlDiffModal listens to.
   * @param oldXml - Current XML state
   * @param newXml - Proposed XML state
   */
  proposeXmlChange(oldXml: string, newXml: string): void {
    const diff = this.computeXmlDiff(oldXml, newXml);

    window.dispatchEvent(new CustomEvent('zp-editor:show-diff', {
      detail: { diff }
    }));
  }

  /**
   * Compute diff between old and new XML.
   * @param oldXml - Current XML
   * @param newXml - Proposed XML
   * @returns Diff object for display
   */
  private computeXmlDiff(oldXml: string, newXml: string): any {
    // Simple line-based diff for now
    // TODO: Implement proper XML-aware diff
    return {
      oldXml,
      newXml,
      changes: [] // Placeholder for structured diff
    };
  }

  /**
   * Highlight nodes in the editor with visual feedback.
   * @param nodeIds - Array of node IDs to highlight
   * @param options - Highlight options (duration, color, pulse)
   */
  highlightNodes(nodeIds: string[], options?: HighlightOptions): void {
    const duration = options?.duration || 3000;
    const color = options?.color || '#3fb950';
    const pulse = options?.pulse !== false;

    nodeIds.forEach(id => {
      const node = document.querySelector(`[data-id="${id}"]`);
      if (node) {
        (node as HTMLElement).classList.add('ai-highlighted');
        (node as HTMLElement).style.setProperty('--highlight-color', color);
        if (pulse) (node as HTMLElement).classList.add('ai-pulse');
      }
    });

    setTimeout(() => {
      nodeIds.forEach(id => {
        const node = document.querySelector(`[data-id="${id}"]`);
        if (node) {
          (node as HTMLElement).classList.remove('ai-highlighted', 'ai-pulse');
        }
      });
    }, duration);
  }

  /**
   * Get direct access to Zustand store (for advanced use).
   * @returns Zustand store instance
   */
  getStore(): any {
    return useGraphStore;
  }
}

/**
 * Singleton instance of the API.
 */
export const zpEditorAPI = new ZpEditorAPIImpl();

/**
 * Initialize window API.
 * Call this once when the app starts.
 * Exposes ZpEditorAPI and store to window for vanilla JS access.
 */
export function initWindowAPI(): void {
  (window as any).ZpEditorAPI = zpEditorAPI;
  (window as any).zpEditorStore = useGraphStore;
  console.log('[ZpEditor] Window API initialized, version:', zpEditorAPI.version);
}

/**
 * Global window declarations for TypeScript.
 */
declare global {
  interface Window {
    ZpEditorAPI: ZpEditorAPI;
    zpEditorStore: any;
    AiPanel?: any;
  }
}
