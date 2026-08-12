const messages: Record<string, string> = {
  configuration_required: 'Configuration is required. Tessera preserved your request and history. Open Settings or Accounts, complete configuration, then retry.',
  provider_auth_required: 'The provider rejected the credential. Tessera preserved your message or Job history. Reconnect the affected Account, then retry.',
  account_unavailable: 'The required Account is unavailable or not granted. Tessera preserved the request and did not call the provider. Restore the Account or update the grant, then retry.',
  plugin_disabled: 'The integration is disabled. Tessera preserved the request and did not execute it. Enable the Plugin, then retry.',
  capability_unavailable: 'The requested capability is unavailable. Tessera preserved the request and did not execute it. Check the Plugin and Account permissions, then retry.',
  rate_limited: 'The provider rate-limited this request. Tessera preserved your message or Job history. Wait before retrying.',
  provider_timeout: 'The provider did not finish in time. Tessera preserved your message or Job history. Retry when the provider is available.',
  provider_unavailable: 'The provider is unavailable. Tessera preserved your message or Job history. Check the connection and retry.',
  provider_malformed: 'The provider returned an invalid response. Tessera preserved your message or Job history and discarded the response. Check provider compatibility before retrying.',
  provider_result_too_large: 'The provider response exceeded Tessera’s safety bound. Tessera preserved the request and discarded the oversized result. Narrow the request, then retry.',
  provider_unsafe_content: 'The provider response failed Tessera’s content safety checks. Tessera preserved the request and did not store the response. Inspect the provider data before retrying.',
  execution_stopped: 'Generation was stopped. Tessera preserved your conversation and recorded the stopped turn. Retry when ready.',
  job_execution_failed: 'The Job failed. Tessera preserved its schedule, grants, and run history. Open the run details, correct the dependency, then run it again.',
  scheduler_pass_failed: 'The scheduler is unavailable. Tessera preserved Job schedules and run history. Check runtime status before retrying.',
  job_canceled: 'The Job was canceled. Tessera preserved its run history and performed no further work.',
}

export function recoveryMessage(code: string | null | undefined, fallback = 'The operation did not complete.') {
  if (!code) return fallback
  return messages[code] ?? `${code.replaceAll('_', ' ')}. Tessera preserved durable product state. Review the affected Account, Plugin, or Job before retrying.`
}
