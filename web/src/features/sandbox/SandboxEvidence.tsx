import { Alert, Collapse, Typography } from 'antd';
/** Protected, bounded script evidence. React escapes output; it never becomes agent context. */
export function SandboxEvidence({ stdout, stderr, truncated, label = 'Initialization' }: { stdout: string; stderr: string; truncated: boolean; label?: string }) {
  return <>
    <Collapse items={[
      { key:'stdout', label:`${label} standard output`, children:<Typography.Paragraph copyable={!!stdout} style={{ whiteSpace:'pre-wrap', overflowWrap:'anywhere' }}>{stdout || 'No standard output.'}</Typography.Paragraph> },
      { key:'stderr', label:`${label} standard error`, children:<Typography.Paragraph copyable={!!stderr} style={{ whiteSpace:'pre-wrap', overflowWrap:'anywhere' }}>{stderr || 'No standard error.'}</Typography.Paragraph> },
    ]} />
    {truncated && <Alert type="info" showIcon title="Initialization output was truncated at the bounded limit." />}
  </>;
}
