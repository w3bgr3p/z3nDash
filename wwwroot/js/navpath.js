window.NAV_CONFIG = {
    hotkeys: {
        'shift+ctrl+Digit1': '/?page=scheduler',
        'shift+ctrl+Digit2': '/?page=pm',
        'shift+ctrl+Digit3': '/?page=zb',
        'shift+ctrl+Digit4': '/?page=logs',
        'shift+ctrl+Digit5': '/?page=http',
        'shift+ctrl+Digit6': '/json',
        'shift+ctrl+Digit7':  '/text.html',
        'shift+ctrl+Digit0': '/?page=config',
        
        'shift+ctrl+KeyC':   '/?page=cliplates',
        'shift+ctrl+KeyH':   '/?page=docs',
        
    },
    items: [
        { id: 'help',      label: 'Help',       href: '/docs',           hotkey: 'H' },
        { id: 'scheduler', label: 'Scheduler',  href: '/scheduler.html', hotkey: '1' },
        { id: 'pm',        label: 'PM',         href: '/?page=pm',       hotkey: '2' },
        { id: 'zb',        label: 'Zb',         href: '/?page=zb',       hotkey: '3' },
        { id: 'logs',      label: 'Logs',       href: '/?page=logs',     hotkey: '4' },
        { id: 'http',      label: 'HTTP',       href: '/?page=http',     hotkey: '5' },
        { id: 'json',      label: 'JSON',       href: '/json',           hotkey: '6' },
        { id: 'text',      label: 'Text',       href: '/?page=text',     hotkey: '7' },
        { id: 'config',    label: 'Config',     href: '/?page=config',   hotkey: '0' },
    ],
};