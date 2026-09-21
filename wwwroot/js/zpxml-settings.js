/* zpxml-settings.js — настройки шаблона: InputSettings проекта в модальном окне.
   Разбор полей берётся из zp-settings.js, тот же, что у страницы zp7, — чтобы
   типы полей и правила подписи не разъезжались между страницами. */

let tsFields = [];

/// XML блока <InputSettings> из открытого документа. Пусто — блока нет.
function tsInputSettingsXml() {
    if (!ZpDoc.doc) return '';
    const el = ZpDoc.doc.querySelector('StaticTechnologies > InputSettings');
    return el ? new XMLSerializer().serializeToString(el) : '';
}

let tsTab = 'input';

function tsOpen() {
    if (!ZpDoc.doc) { setStatus('шаблон не открыт', 'err'); return; }
    document.getElementById('ts-modal').classList.add('open');
    tsShowTab(tsTab);
}

function tsShowTab(tab) {
    tsTab = tab;
    document.querySelectorAll('.ts-tab').forEach(b => b.classList.toggle('active', b.dataset.ts === tab));
    document.getElementById('ts-apply').style.display = tab === 'input' ? '' : 'none';
    document.getElementById('ts-body').innerHTML =
        tab === 'input' ? tsRenderInput() : tsRenderProfile();
}

function tsRenderInput() {
    const xml = tsInputSettingsXml();
    if (!xml) return '<div class="ts-empty">в шаблоне нет блока InputSettings</div>';
    try {
        tsFields = ZpSettings.parseFieldsXml(xml, {});
    } catch (e) {
        return '<div class="ts-empty">input settings: ' + escHtml(e.message) + '</div>';
    }
    return tsRenderFields(tsFields);
}

/// Правила генерации личности из <Profile>. Только чтение: это правила
/// ProjectMaker, и менять их отсюда — отдельная работа.
/// Отмечено, что из них умеет рантайм: остальное лежит в файле, но на
/// генерацию пока не влияет, и делать вид, что влияет, нельзя.
const TS_PROFILE_USED = new Set([
    'Nationality', 'Regions', 'Males', 'MinAge', 'MaxAge', 'LoginGenerationRule'
]);

function tsRenderProfile() {
    const el = ZpDoc.doc.querySelector('StaticTechnologies > Profile');
    if (!el) return '<div class="ts-empty">в шаблоне нет блока Profile</div>';

    const skip = new Set(['orderNumber', 'IsSettingControlVisible', 'SettingsTabNumber', 'MinVersion']);
    const rows = [...el.attributes].filter(a => !skip.has(a.name));
    if (!rows.length) return '<div class="ts-empty">в блоке нет правил</div>';

    const used = rows.filter(a => TS_PROFILE_USED.has(a.name));
    const rest = rows.filter(a => !TS_PROFILE_USED.has(a.name));

    const table = list => '<div class="dl params ts-profile">' + list.map(a =>
        '<dt>' + escHtml(a.name) + '</dt><dd>' + escHtml(cut(a.value, 300)) + '</dd>').join('') + '</div>';

    return '<div class="ts-divider">используется при генерации</div>' + table(used) +
           '<div class="ts-divider">есть в файле, генерацией не используется</div>' + table(rest);
}

function tsClose() {
    document.getElementById('ts-modal').classList.remove('open');
}

/// Поля по типам — как в zp7: Boolean переключателем, DropDown списком,
/// длинное значение — textarea. Tab, Comment и Label ключа не имеют и служат
/// разделителями.
function tsRenderFields(fields) {
    let html = '';
    for (const field of fields) {
        if (field.type === 'Comment' || (!field.key && field.type === 'Label')) {
            const text = (field.label || '').replace(/[▪◆►•]+/g, '').replace(/\s+/g, ' ').trim();
            if (text.length > 1) html += '<div class="ts-divider">' + escHtml(text.slice(0, 100)) + '</div>';
            continue;
        }
        if (!field.key) continue;

        const key   = escHtml(field.key);
        const value = field.value || '';
        const help  = field.help ? ' title="' + escHtml(field.help) + '"' : '';
        const label = '<span class="ts-label">' + escHtml(ZpSettings.cleanLabel(field)) +
                      '<span class="ts-key">' + key + '</span></span>';
        const options = ZpSettings.fieldOptions(field.label);

        if (field.type === 'Boolean') {
            const checked = /^true$/i.test(value) ? ' checked' : '';
            html += '<div class="ts-field"' + help + '>' + label +
                    '<input type="checkbox" data-key="' + key + '"' + checked + '></div>';
        } else if ((field.type === 'DropDown' || field.type === 'Select') && options) {
            const opts = options.map(o =>
                '<option value="' + escHtml(o) + '"' + (o === value ? ' selected' : '') + '>' +
                (o === '' ? '—' : escHtml(o)) + '</option>').join('');
            html += '<div class="ts-field"' + help + '>' + label +
                    '<select data-key="' + key + '">' + opts + '</select></div>';
        } else if (field.type === 'DropDownMultiSelect' && options) {
            const selected = String(value).split(',').map(s => s.trim());
            const boxes = options.map(o =>
                '<label class="ts-multi-item"><input type="checkbox" data-multi="' + key +
                '" value="' + escHtml(o) + '"' + (selected.includes(o) ? ' checked' : '') + '>' +
                escHtml(o) + '</label>').join('');
            html += '<div class="ts-field"' + help + '>' + label +
                    '<div class="ts-multi">' + boxes + '</div></div>';
        } else if (value.length > 60) {
            html += '<div class="ts-field"' + help + '>' + label +
                    '<textarea data-key="' + key + '" spellcheck="false">' + escHtml(value) + '</textarea></div>';
        } else {
            const type = field.type === 'Password' ? 'password' : 'text';
            html += '<div class="ts-field"' + help + '>' + label +
                    '<input type="' + type + '" data-key="' + key + '" value="' + escHtml(value) +
                    '" spellcheck="false"></div>';
        }
    }
    return html || '<div class="ts-empty">в блоке нет полей</div>';
}

// ── Сохранение ───────────────────────────────────────────────────────────────

/// Собрать значения из формы: ключ поля — имя переменной из OutputVariable.
function tsCollect() {
    const values = {};
    document.querySelectorAll('#ts-body [data-key]').forEach(el => {
        values[el.dataset.key] = el.type === 'checkbox'
            ? (el.checked ? 'True' : 'False')
            : el.value;
    });
    const multi = {};
    document.querySelectorAll('#ts-body [data-multi]').forEach(el => {
        if (!multi[el.dataset.multi]) multi[el.dataset.multi] = [];
        if (el.checked) multi[el.dataset.multi].push(el.value);
    });
    Object.keys(multi).forEach(k => { values[k] = multi[k].join(','); });
    return values;
}

/// Записать значения в документ. Правится DefaultValue — то, с чем шаблон
/// стартует; сами поля, их типы и списки вариантов не трогаем.
function tsApply() {
    const values = tsCollect();
    const changed = ZpDoc.setInputDefaults(values);
    tsClose();
    if (changed > 0) refresh('input settings: изменено полей ' + changed);
    else setStatus('input settings: без изменений', 'info');
}

(function () {
    document.getElementById('btn-settings').addEventListener('click', tsOpen);
    document.querySelectorAll('.ts-tab').forEach(b =>
        b.addEventListener('click', () => tsShowTab(b.dataset.ts)));
    document.getElementById('ts-close').addEventListener('click', tsClose);
    document.getElementById('ts-cancel').addEventListener('click', tsClose);
    document.getElementById('ts-apply').addEventListener('click', tsApply);

    // Клавиши формы не должны доходить до холста: Delete там удаляет ветку.
    document.getElementById('ts-modal').addEventListener('keydown', e => {
        e.stopPropagation();
        if (e.key === 'Escape') tsClose();
    });
    document.getElementById('ts-modal').addEventListener('mousedown', e => {
        if (e.target.id === 'ts-modal') tsClose();      // клик по подложке
    });
})();
