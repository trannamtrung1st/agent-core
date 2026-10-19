export type OfficeProposal = { title: string; disclosure: string; fields: { label: string; text: string }[] };
type Obj = Record<string, unknown>;
const object = (v: unknown): v is Obj => !!v && typeof v === 'object' && !Array.isArray(v);
const keys = (v: Obj, allowed: string[]) => Object.keys(v).every(k => allowed.includes(k));
const optional = (v: Obj, key: string, type: string) => v[key] === undefined || typeof v[key] === type;
const list = (v: unknown, max: number, check: (item: unknown) => boolean, min = 0): v is unknown[] => Array.isArray(v) && v.length >= min && v.length <= max && v.every(check);
const strings = (v: unknown, max: number) => list(v, max, item => typeof item === 'string');
const cell = (v: unknown) => object(v) && keys(v, ['type', 'text', 'number', 'boolean', 'formula', 'format'])
  && ['text', 'number', 'boolean', 'date', 'blank'].includes(String(v.type))
  && ['text', 'formula', 'format'].every(k => optional(v, k, 'string')) && optional(v, 'boolean', 'boolean')
  && (v.number === undefined || typeof v.number === 'number' && Number.isFinite(v.number));
const rows = (v: unknown) => list(v, 200, row => list(row, 64, cell));
const value = (v: unknown) => typeof v === 'boolean' ? v ? 'Yes' : 'No' : v === '' ? '(empty text)' : String(v);
const cellLabels: Record<string, string> = { type: 'Type', text: 'Text', number: 'Number', boolean: 'Boolean', formula: 'Stored formula', format: 'Number format' };
function cellText(input: unknown) {
  return (input as Obj[][]).map((row, r) => row.length === 0 ? `Row ${r + 1}: empty` : row.map((c, col) => `Row ${r + 1}, column ${col + 1}\n${Object.keys(cellLabels).filter(k => c[k] !== undefined).map(k => `${cellLabels[k]}: ${value(c[k])}`).join('\n')}`).join('\n\n')).join('\n\n');
}
export function savedOfficeMutation(tool: string, args: Obj): OfficeProposal | null {
  const document = tool.startsWith('office.document.'); const create = tool.endsWith('.create');
  if (!['office.document.create', 'office.document.edit', 'office.spreadsheet.create', 'office.spreadsheet.edit'].includes(tool)
    || typeof args.path !== 'string' || !/^\/(home|working)\//.test(args.path) || !args.path.toLowerCase().endsWith(document ? '.docx' : '.xlsx')) return null;
  const fields: OfficeProposal['fields'] = []; const add = (label: string, text: unknown) => fields.push({ label, text: String(text) });
  if (create && document) {
    if (!keys(args, ['path', 'title', 'sections']) || !optional(args, 'title', 'string') || !list(args.sections, 20, s => object(s)
      && keys(s, ['heading', 'paragraphs', 'table', 'pageBreakBefore']) && optional(s, 'heading', 'string') && optional(s, 'pageBreakBefore', 'boolean')
      && (s.paragraphs === undefined || strings(s.paragraphs, 100)) && (s.table === undefined || list(s.table, 200, row => strings(row, 20))), 1)) return null;
    if (args.title !== undefined) add('Document title', args.title);
    args.sections.forEach((item, index) => {
      const s = item as Obj; const label = `Section ${index + 1}`;
      if (s.heading !== undefined) add(`${label} · heading`, s.heading);
      if (s.paragraphs !== undefined) add(`${label} · paragraphs in order`, (s.paragraphs as string[]).map((p, i) => `Paragraph ${i + 1}\n${value(p)}`).join('\n\n') || 'No paragraphs');
      if (s.table !== undefined) add(`${label} · table cells in row order`, (s.table as string[][]).map((row, r) => row.map((c, col) => `Row ${r + 1}, column ${col + 1}\n${value(c)}`).join('\n\n') || `Row ${r + 1}: empty`).join('\n\n') || 'No rows');
      add(`${label} · page break before`, s.pageBreakBefore === true ? 'Yes' : 'No');
    });
  } else if (create) {
    if (!keys(args, ['path', 'sheets']) || !list(args.sheets, 50, s => object(s) && keys(s, ['name', 'rows']) && typeof s.name === 'string' && rows(s.rows), 1)) return null;
    args.sheets.forEach((item, index) => { const s = item as Obj; add(`Sheet ${index + 1} · name`, s.name); add(`Sheet ${index + 1} · typed cells in row order`, cellText(s.rows) || 'No stored cells'); });
  } else {
    if (!keys(args, ['path', 'operations']) || !list(args.operations, 20, o => object(o), 1)) return null;
    for (const [index, item] of args.operations.entries()) {
      const o = item as Obj;
      const allowed = document ? ['operation', 'selector', 'text', 'style'] : ['operation', 'sheet', 'range', 'cells', 'name', 'formula', 'format'];
      const operations = document ? ['replace_paragraph', 'insert_paragraph_after', 'replace_table_cell', 'set_heading_style'] : ['set_cells', 'add_sheet', 'rename_sheet', 'set_formula', 'set_number_format', 'create_table'];
      if (!keys(o, allowed) || !operations.includes(String(o.operation)) || typeof o[document ? 'selector' : 'sheet'] !== 'string'
        || !allowed.filter(k => k !== 'cells').every(k => optional(o, k, 'string')) || o.cells !== undefined && !rows(o.cells)) return null;
      const names: Record<string, string> = { operation: 'Operation', selector: 'Exact selector', text: 'Replacement text', style: 'Paragraph style', sheet: 'Sheet', range: 'Exact range', cells: 'Typed cells in range order', name: 'New sheet or table name', formula: 'Stored formula', format: 'Number format' };
      const operationNames: Record<string, string> = { replace_paragraph: 'Replace paragraph', insert_paragraph_after: 'Insert paragraph after', replace_table_cell: 'Replace table cell', set_heading_style: 'Set paragraph style', set_cells: 'Set cells', add_sheet: 'Add sheet', rename_sheet: 'Rename sheet', set_formula: 'Set stored formula', set_number_format: 'Set number format', create_table: 'Create table' };
      for (const key of allowed) if (o[key] !== undefined) add(`Edit ${index + 1} · ${names[key]}`, key === 'cells' ? cellText(o.cells) || 'No stored cells' : key === 'operation' ? operationNames[String(o.operation)] : o[key]);
    }
  }
  return { title: `${create ? 'Create' : 'Edit'} ${document ? 'Word document' : 'Excel workbook'}`, fields,
    disclosure: `${create ? 'Creates a new file and never replaces an occupied path.' : 'Edits only the exact saved source; current source-read permission is still required.'} Stored formulas are not calculated. No Artifact is published.` };
}
