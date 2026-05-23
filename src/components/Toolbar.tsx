import { useRef, useState } from 'react';
import { useGraphStore } from '../store/graphStore';
import { useUIStore } from '../store/uiStore';
import { useHistoryStore } from '../store/historyStore';
import { parseZenPosterXML, zpGraphToReactFlow } from '../utils/xmlParser';
import { reactFlowToZPGraph, generateZenPosterXML } from '../utils/xmlGenerator';
import { applyDagreLayout } from '../utils/layoutEngine';
import { zpServer } from '../api/zpServerClient';
import ZpServerDialog from './ZpServerDialog';

export default function Toolbar() {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const { nodes, edges, setNodes, setEdges, restoreSnapshot } = useGraphStore();
  const { layoutDirection, setLayoutDirection, toggleSearch, theme, setTheme } = useUIStore();
  const { undo, redo, canUndo, canRedo } = useHistoryStore();
  const [showZpDialog, setShowZpDialog] = useState<'load' | 'save' | null>(null);

  const handleFileLoad = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (e) => {
      const xmlString = e.target?.result as string;
      const { graph, error } = parseZenPosterXML(xmlString);

      if (error) {
        alert(`Ошибка парсинга XML: ${error}`);
        return;
      }

      const { nodes, edges } = zpGraphToReactFlow(graph);
      const layoutedNodes = applyDagreLayout(nodes, edges, layoutDirection);

      setNodes(layoutedNodes);
      setEdges(edges);
    };

    reader.readAsText(file, 'UTF-8');
  };

  const handleUndo = () => {
    const snapshot = undo();
    if (snapshot) {
      restoreSnapshot(snapshot.nodes, snapshot.edges);
    }
  };

  const handleRedo = () => {
    const snapshot = redo();
    if (snapshot) {
      restoreSnapshot(snapshot.nodes, snapshot.edges);
    }
  };

  const handleSave = () => {
    if (nodes.length === 0) {
      alert('Нет данных для сохранения');
      return;
    }

    try {
      // Convert React Flow graph to ZP format
      const zpGraph = reactFlowToZPGraph({ nodes, edges });

      // Generate XML string
      const xmlString = generateZenPosterXML(zpGraph);

      // Create blob and download
      const blob = new Blob([xmlString], { type: 'text/xml;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;

      // Generate filename with timestamp
      const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
      link.download = `zp-flow-${timestamp}.xml`;

      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    } catch (error) {
      alert(`Ошибка сохранения: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  };

  const handleRelayout = () => {
    if (nodes.length === 0) return;
    const layoutedNodes = applyDagreLayout(nodes, edges, layoutDirection);
    setNodes(layoutedNodes);
  };

  const handleToggleDirection = () => {
    const newDirection = layoutDirection === 'TB' ? 'LR' : 'TB';
    setLayoutDirection(newDirection);
    const layoutedNodes = applyDagreLayout(nodes, edges, newDirection);
    setNodes(layoutedNodes);
  };

  const handleZpServerLoad = async (path: string) => {
    try {
      const xml = await zpServer.getTaskXml(path);
      const { graph, error } = parseZenPosterXML(xml);

      if (error) {
        alert(`Ошибка парсинга XML: ${error}`);
        return;
      }

      const { nodes, edges } = zpGraphToReactFlow(graph);
      const layoutedNodes = applyDagreLayout(nodes, edges, layoutDirection);

      setNodes(layoutedNodes);
      setEdges(edges);
      setShowZpDialog(null);
    } catch (error) {
      alert(`Ошибка загрузки: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  };

  const handleZpServerSave = async (path: string) => {
    if (nodes.length === 0) {
      alert('Нет данных для сохранения');
      return;
    }

    try {
      const zpGraph = reactFlowToZPGraph({ nodes, edges });
      const xmlString = generateZenPosterXML(zpGraph);
      await zpServer.saveTaskXml(path, xmlString);
      setShowZpDialog(null);
      alert('✓ Сохранено успешно');
    } catch (error) {
      alert(`Ошибка сохранения: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  };

  return (
    <div style={{
      background: 'var(--bg1)',
      borderBottom: '1px solid var(--border)',
      padding: '6px 16px',
      display: 'flex',
      gap: '8px',
      alignItems: 'center',
      justifyContent: 'space-between',
    }}>
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
        <input
          ref={fileInputRef}
          type="file"
          accept=".xml,.zp,.txt"
          style={{ display: 'none' }}
          onChange={handleFileLoad}
        />
        <button
          onClick={() => fileInputRef.current?.click()}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
          title="Открыть XML файл"
        >
          📂
        </button>
        <button
          onClick={handleSave}
          disabled={nodes.length === 0}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: nodes.length === 0 ? 0.5 : 1,
            cursor: nodes.length === 0 ? 'not-allowed' : 'pointer',
          }}
          title="Сохранить как XML"
        >
          💾
        </button>
        <div style={{ width: '1px', height: '16px', background: 'var(--border)' }} />
        <button
          onClick={() => setShowZpDialog('load')}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
          title="Загрузить из ZennoPoster"
        >
          📥 ZP
        </button>
        <button
          onClick={() => setShowZpDialog('save')}
          disabled={nodes.length === 0}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: nodes.length === 0 ? 0.5 : 1,
            cursor: nodes.length === 0 ? 'not-allowed' : 'pointer',
          }}
          title="Сохранить в ZennoPoster"
        >
          📤 ZP
        </button>
        <div style={{ width: '1px', height: '16px', background: 'var(--border)' }} />
        <button
          onClick={handleUndo}
          disabled={!canUndo()}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: canUndo() ? 1 : 0.5,
            cursor: canUndo() ? 'pointer' : 'not-allowed',
          }}
          title="Отменить"
        >
          ↶
        </button>
        <button
          onClick={handleRedo}
          disabled={!canRedo()}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: canRedo() ? 1 : 0.5,
            cursor: canRedo() ? 'pointer' : 'not-allowed',
          }}
          title="Повторить"
        >
          ↷
        </button>
        <div style={{ width: '1px', height: '16px', background: 'var(--border)' }} />
        <button
          onClick={handleRelayout}
          disabled={nodes.length === 0}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: nodes.length === 0 ? 0.5 : 1,
            cursor: nodes.length === 0 ? 'not-allowed' : 'pointer',
          }}
          title="Перестроить граф"
        >
          🧹
        </button>
        <button
          onClick={handleToggleDirection}
          disabled={nodes.length === 0}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            opacity: nodes.length === 0 ? 0.5 : 1,
            cursor: nodes.length === 0 ? 'not-allowed' : 'pointer',
          }}
          title={layoutDirection === 'TB' ? 'Переключить на горизонталь' : 'Переключить на вертикаль'}
        >
          {layoutDirection === 'TB' ? '↔️' : '↕️'}
        </button>
        <div style={{ width: '1px', height: '16px', background: 'var(--border)' }} />
        <button
          onClick={toggleSearch}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
          title="Поиск"
        >
          🔍
        </button>
        <button
          onClick={() => window.dispatchEvent(new CustomEvent('zp-editor:open-ai-panel'))}
          style={{
            padding: '4px 12px',
            fontSize: '11px',
            background: 'var(--bg2)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
          title="Открыть AI панель"
        >
          🤖 AI
        </button>
      </div>

      <div style={{ display: 'flex', gap: '8px' }}>
        <button
          onClick={() => setTheme('dark')}
          style={{
            padding: '4px 10px',
            fontSize: '11px',
            background: theme === 'dark' ? 'var(--accent)' : 'var(--bg2)',
            color: theme === 'dark' ? '#fff' : 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
        >
          🌙
        </button>
        <button
          onClick={() => setTheme('light')}
          style={{
            padding: '4px 10px',
            fontSize: '11px',
            background: theme === 'light' ? 'var(--accent)' : 'var(--bg2)',
            color: theme === 'light' ? '#000' : 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
        >
          ☀️
        </button>
        <button
          onClick={() => setTheme('hyper')}
          style={{
            padding: '4px 10px',
            fontSize: '11px',
            background: theme === 'hyper' ? 'var(--accent)' : 'var(--bg2)',
            color: theme === 'hyper' ? '#000' : 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--radius)',
            cursor: 'pointer',
          }}
        >
          ⚡
        </button>
      </div>

      {showZpDialog && (
        <ZpServerDialog
          mode={showZpDialog}
          onClose={() => setShowZpDialog(null)}
          onConfirm={showZpDialog === 'load' ? handleZpServerLoad : handleZpServerSave}
        />
      )}
    </div>
  );
}
