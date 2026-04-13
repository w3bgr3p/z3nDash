window.NAV_CONFIG = {
    hotkeys: {
        'alt+Digit1': '/?page=scheduler',
        'alt+Digit2': '/?page=zp7',
        'alt+Digit3': '/?page=zb',
        'alt+Digit4': '/?page=logs',
        'alt+Digit5': '/?page=http',
        'alt+Digit6': '/json',
        'alt+Digit7': '/text.html',
         
        'alt+Digit9': '/ai.html',
        'alt+Digit0': '/?page=config',
        'alt+KeyC':   '/?page=clips',
        'alt+KeyH':   '/docs',
    },
    items: [

        { id: 'scheduler', label: 'Scheduler',  href: '/scheduler.html', hotkey: '1' },
        { id: 'zp7',       label: 'ZP7',         href: '/?page=zp7',     hotkey: '2' },
        { id: 'zb',        label: 'ZB',         href: '/?page=zb',       hotkey: '3' },
        { id: 'logs',      label: 'Logs',       href: '/?page=logs',     hotkey: '4' },
        { id: 'http',      label: 'HTTP',       href: '/?page=http',     hotkey: '5' },
        { id: 'json',      label: 'JSON',       href: '/json',           hotkey: '6' },
        { id: 'text',      label: 'Text',       href: '/?page=text',     hotkey: '7' },
        { id: 'clips',     label: 'Clips',      href: '/?page=clips',    hotkey: 'C' },
        { id: 'ai',        label: 'Ai',         href: '/?page=ai',       hotkey: '9' },
        { id: 'treasury',        label: 'Treasury',         href: '/?page=treasury',       hotkey: '8' },
        { id: 'system',        label: 'System',         href: '/?page=system',       hotkey: 'S' },
        { id: 'config',    label: 'Config',     href: '/?page=config',   hotkey: '0' },
        { id: 'help',      label: 'Docs',       href: '/docs',           hotkey: 'H' },
        { id: 'graph',      label: 'Grapher',       href: '/?page=graph',           hotkey: 'G' },
        //{ id: 'terminal',      label: 'Terminal',       href: '/?page=terminal',           hotkey: 'R' },
    ],
};