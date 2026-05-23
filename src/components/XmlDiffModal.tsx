import React, { useEffect, useState } from 'react';
import { renderDiffHtml } from '../utils/xmlDiff';
import { parseZenPosterXML, zpGraphToReactFlow } from '../utils/xmlParser';
import { useGraphStore } from '../store/graphStore';
import './XmlDiffModal.css';

interface XmlDiff {
  oldXml: string;
  newXml: string;
  operation: 'insert' | 'replace' | 'delete';
  description: string;
}

interface XmlDiffModalProps {}

export const XmlDiffModal: React.FC<XmlDiffModalProps> = () => {
  const [isOpen, setIsOpen] = useState(false);
  const [diff, setDiff] = useState<XmlDiff | null>(null);
  const [diffHtml, setDiffHtml] = useState('');
  const { setNodes, setEdges } = useGraphStore();

  useEffect(() => {
    const handleShowDiff = (event: CustomEvent) => {
      const { diff } = event.detail;
      setDiff(diff);
      setDiffHtml(renderDiffHtml(diff));
      setIsOpen(true);
    };

    window.addEventListener('zp-editor:show-diff', handleShowDiff as EventListener);

    return () => {
      window.removeEventListener('zp-editor:show-diff', handleShowDiff as EventListener);
    };
  }, []);

  const handleApply = () => {
    if (!diff) return;

    // Parse new XML and update editor state
    const { graph, error } = parseZenPosterXML(diff.newXml);

    if (error) {
      console.error('[XmlDiffModal] Failed to parse new XML:', error);
      alert(`Failed to apply changes: ${error}`);
      return;
    }

    // Convert ZP graph to React Flow nodes/edges
    const { nodes, edges } = zpGraphToReactFlow(graph);

    // Update editor state
    setNodes(nodes);
    setEdges(edges);

    // TODO: Highlight affected nodes after applying
    // This will require comparing old and new node sets

    setIsOpen(false);
    setDiff(null);
  };

  const handleCancel = () => {
    setIsOpen(false);
    setDiff(null);
  };

  if (!isOpen) return null;

  return (
    <div className="xml-diff-modal-overlay">
      <div className="xml-diff-modal">
        <div className="xml-diff-modal-header">
          <h2>Review XML Changes</h2>
          <button className="xml-diff-modal-close" onClick={handleCancel}>
            ×
          </button>
        </div>

        {diff && (
          <div className="xml-diff-modal-description">
            <strong>Operation:</strong> {diff.operation} | <strong>Description:</strong> {diff.description}
          </div>
        )}

        <div
          className="xml-diff-modal-content"
          dangerouslySetInnerHTML={{ __html: diffHtml }}
        />

        <div className="xml-diff-modal-footer">
          <button className="xml-diff-modal-button cancel" onClick={handleCancel}>
            Cancel
          </button>
          <button className="xml-diff-modal-button apply" onClick={handleApply}>
            Apply Changes
          </button>
        </div>
      </div>
    </div>
  );
};
