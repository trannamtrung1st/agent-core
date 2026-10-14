import { Component, type ReactNode } from 'react';
import { Alert, Button } from 'antd';
// Keeps the original Download and Close controls alive when a lazy renderer fails.
export class PreviewRenderBoundary extends Component<{ children: ReactNode; onRetry: () => void }, { failed: boolean; reload: boolean }> {
  state = { failed: false, reload: false };
  static getDerivedStateFromError(error: unknown) {
    const message = error instanceof Error ? error.message : '';
    return { failed: true, reload: /dynamically imported module|module script|Loading chunk|preload CSS|Loading CSS chunk/i.test(message) };
  }
  render() {
    return this.state.failed ? <Alert type="warning" showIcon
      title={this.state.reload ? 'The preview component could not be loaded. Reload the application or download the original.' : 'The preview could not be rendered. Retry or download the original.'}
      action={<Button onClick={this.state.reload ? () => window.location.reload() : this.props.onRetry}>{this.state.reload ? 'Reload application' : 'Retry'}</Button>} /> : this.props.children;
  }
}
