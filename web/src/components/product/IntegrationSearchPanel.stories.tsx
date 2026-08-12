import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { IntegrationSearchPanel } from './IntegrationSearchPanel'

const sources = [{ id: 'local', name: 'Installed and local', state: 'READY', errorCode: null }, { id: 'registry', name: 'MCP Registry', state: 'READY', errorCode: null }]
const items = [{ id: 'example/mail', name: 'Mail MCP', description: 'Read mailbox metadata through a public MCP server.', source: 'registry', publisher: 'example', runtime: 'MCP', repositoryOrPackage: 'https://code.example/example/mail', version: '1.2.3', license: 'MIT', trustLevel: 'VERIFIED_METADATA', capabilitiesSummary: ['Read mailbox metadata'], authTypes: ['External credentials'], sensitivity: 'PERSONAL_DATA', installationMode: 'SERVER_REVIEW_REQUIRED', installState: 'REVIEW_REQUIRED', installed: false, inspectUrl: 'https://code.example/example/mail' }]

const meta = { title: 'Product/IntegrationSearchPanel', component: IntegrationSearchPanel, parameters: { layout: 'padded', a11y: { test: 'error' } }, args: { query: 'mail', items, sources, loading: false, installingId: null, errorMessage: null, onSearch: () => undefined, onInspect: () => undefined, onInstall: () => undefined } } satisfies Meta<typeof IntegrationSearchPanel>
export default meta
type Story = StoryObj<typeof meta>

export const Results: Story = {}
export const InstallReview: Story = {
	args: { items: [{ ...items[0], id: 'local-mail', source: 'local', trustLevel: 'BUILT_IN', installationMode: 'SERVER_INSTALLED', installState: 'AVAILABLE', installed: false, inspectUrl: null }] },
	play: async ({ canvasElement }) => {
		const canvas = within(canvasElement)
		await userEvent.click(canvas.getByRole('button', { name: 'Review installation' }))
		const page = within(canvasElement.ownerDocument.body)
		await expect(page.getByRole('dialog')).toBeVisible()
		await expect(page.getByRole('heading', { name: 'Install Mail MCP' })).toBeVisible()
	},
}
export const Searching: Story = { args: { loading: true } }
export const Empty: Story = { args: { items: [] } }
export const SourceDegraded: Story = { args: { sources: [{ id: 'registry', name: 'MCP Registry', state: 'DEGRADED', errorCode: 'source_unavailable' }], errorMessage: 'One catalog source is unavailable.' } }
export const PublicSourceUnavailable: Story = { args: { items: [{ ...items[0], id: 'registry-unavailable', source: 'mcp-registry', inspectUrl: null, trustLevel: 'UNTRUSTED' }] } }