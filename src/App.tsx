import { useEffect } from 'react';
import { ReactFlowProvider } from 'reactflow';
import { useUIStore } from './store/uiStore';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts';
import { initWindowApi } from './api/windowApi';
import Toolbar from './components/Toolbar';
import Canvas from './components/Canvas';
import SidePanel from './components/SidePanel';
import SearchPanel from './components/SearchPanel';
import { XmlDiffModal } from './components/XmlDiffModal';
import './components/XmlDiffModal.css';

function App() {
  const theme = useUIStore((state) => state.theme);
  useKeyboardShortcuts();

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);

  useEffect(() => {
    initWindowApi();
  }, []);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <Toolbar />
      <ReactFlowProvider>
        <div style={{ display: 'flex', flex: 1, overflow: 'hidden', position: 'relative' }}>
          <Canvas />
          <SidePanel />
          <SearchPanel />
        </div>
      </ReactFlowProvider>
      <XmlDiffModal />
    </div>
  );
}

export default App;
