import { openTrustedExternal } from '../app/runtime'
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  Blocks,
  Brain,
  CalendarClock,
  KeyRound,
  Loader2,
  Settings2,
  Wallet,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useState } from "react";
import {
  R2Problem,
  r2Api,
  type R2Account,
  type R2Action,
  type R2Job,
  type R2Memory,
  type R2Plugin,
} from "../api/r2";
import {
  ActionApprovalCard,
  JobRunTimeline,
  ProductStateBadge,
} from "../components/product/R2ProductComponents";
import { IntegrationSearchPanel } from "../components/product/IntegrationSearchPanel";
import { Alert, AlertDescription } from "../components/ui/alert";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "../components/ui/dialog";
import { Input } from "../components/ui/input";
import { Label } from "../components/ui/label";
import { recoveryMessage } from "../lib/product-error";

function ProductState({
  title,
  description,
  empty,
  icon: Icon,
  loading,
  error,
  children,
}: {
  title: string;
  description: string;
  empty: string;
  icon: LucideIcon;
  loading: boolean;
  error?: Error | null;
  children?: React.ReactNode;
}) {
  return (
    <section aria-labelledby={`${title}-title`}>
      <header className="border-b border-border pb-4">
        <h1 id={`${title}-title`} className="text-xl font-semibold">
          {title}
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      </header>
      {loading ? (
        <div className="flex items-center gap-2 py-8 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
          Loading {title.toLowerCase()}…
        </div>
      ) : error ? (
        <Alert variant="destructive" className="mt-6">
          <AlertDescription>{error.message}</AlertDescription>
        </Alert>
      ) : (
        (children ?? (
          <div className="mx-auto max-w-md py-16 text-center">
            <Icon
              className="mx-auto h-9 w-9 text-muted-foreground"
              aria-hidden
            />
            <h2 className="mt-4 text-lg font-semibold">{empty}</h2>
          </div>
        ))
      )}
    </section>
  );
}

function problem(error: unknown): string | null {
  if (!error) return null;
  if (error instanceof R2Problem)
    return recoveryMessage(error.code, error.message);
  return error instanceof Error
    ? recoveryMessage(null, error.message)
    : recoveryMessage(null);
}

function isValidTimeZone(value: string): boolean {
  try {
    new Intl.DateTimeFormat("en", { timeZone: value }).format();
    return value.trim().length > 0;
  } catch {
    return false;
  }
}

export function AccountsPage() {
  const client = useQueryClient();
  const setup = useQuery({
    queryKey: ["r2", "setup"],
    queryFn: r2Api.setupStatus,
  });
  const accounts = useQuery({
    queryKey: ["r2", "accounts"],
    queryFn: r2Api.accounts,
    refetchInterval: 3000,
  });
  const plugins = useQuery({
    queryKey: ["r2", "plugins"],
    queryFn: r2Api.plugins,
  });
  const [pluginId, setPluginId] = useState("github");
  const [displayName, setDisplayName] = useState("");
  const [repository, setRepository] = useState("");
  const [secret, setSecret] = useState("");
  const [connectorId, setConnectorId] = useState("");
  const [disable, setDisable] = useState<R2Account | null>(null);
  const [revoke, setRevoke] = useState<R2Account | null>(null);
  const refresh = () => {
    void client.invalidateQueries({ queryKey: ["r2", "setup"] });
    return client.invalidateQueries({ queryKey: ["r2", "accounts"] });
  };
  const rmConnectors = useQuery({
    queryKey: ["r2", "regina-maria-connectors"],
    queryFn: r2Api.reginaMariaConnectors,
    enabled: pluginId === "regina-maria",
  });
  const connect = useMutation({
    mutationFn: async () => {
      if (pluginId === "gmail") return r2Api.beginGmailOAuth(displayName);
      if (pluginId === "regina-maria")
        return r2Api.connectReginaMaria(connectorId, displayName);
      const account = await r2Api.connectAccount({
        pluginId,
        displayName,
        secretInput: secret,
        nonSecretConfig:
          pluginId === "github"
            ? {
                pluginVersion: "1.0.0",
                allowedRepositories: repository
                  .split(",")
                  .map((value) => value.trim())
                  .filter(Boolean),
              }
            : { pluginVersion: "1.0.0" },
      });
      return r2Api.validateAccount(account);
    },
    onSuccess: (result) => {
      if ("authorizeUrl" in result) {
        void openTrustedExternal(result.authorizeUrl);
        return;
      }
      setSecret("");
      setDisplayName("");
      setRepository("");
      setConnectorId("");
      void refresh();
    },
  });
  const lifecycle = useMutation({
    mutationFn: ({
      account,
      operation,
    }: {
      account: R2Account;
      operation: "validate" | "disable" | "revoke";
    }) =>
      operation === "validate"
        ? r2Api.validateAccount(account)
        : operation === "disable"
          ? r2Api.disableAccount(account)
          : r2Api.revokeAccount(account),
    onSuccess: () => {
      setDisable(null);
      setRevoke(null);
      void refresh();
    },
  });
  const items = accounts.data?.items ?? [];
  const accountPlugins = (plugins.data?.items ?? []).filter(
    (plugin) =>
      plugin.id !== "model-provider" &&
      plugin.capabilities.some((capability) => capability.accountRequired),
  );
  return (
    <ProductState
      title="Accounts"
      description="Authorized external identities and model services. Secret values are write-only."
      empty="No connected accounts"
      icon={Wallet}
      loading={accounts.isLoading}
      error={accounts.error}
    >
      {setup.data?.integrations.length ? (
        <section className="border-b border-border py-5" aria-labelledby="integration-readiness-title">
          <h2 id="integration-readiness-title" className="text-sm font-semibold">
            Integration readiness
          </h2>
          <ul className="mt-3 grid gap-2 md:grid-cols-3">
            {setup.data.integrations.map((integration) => (
              <li
                key={integration.id}
                className="flex min-h-16 items-center justify-between gap-3 rounded-md border border-border px-3 py-2"
              >
                <div>
                  <p className="text-sm font-medium">{integration.name}</p>
                  <p className="text-xs text-muted-foreground">
                    {integration.state === "CONNECTED"
                      ? "Account connected"
                      : integration.runtimeState === "READY"
                        ? "Runtime ready; account authorization remains"
                        : "Runtime unavailable"}
                  </p>
                </div>
                <ProductStateBadge state={integration.state} />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      <form
        className="grid gap-3 border-b border-border py-5 md:grid-cols-2"
        onSubmit={(event) => {
          event.preventDefault();
          connect.mutate();
        }}
      >
        <div className="space-y-2">
          <Label htmlFor="account-plugin">Account type</Label>
          <select
            id="account-plugin"
            className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm"
            value={pluginId}
            onChange={(event) => setPluginId(event.target.value)}
          >
            {accountPlugins.map((plugin) => (
              <option key={plugin.id} value={plugin.id}>
                {plugin.name}
              </option>
            ))}
          </select>
        </div>
        <div className="space-y-2">
          <Label htmlFor="account-name">Display name</Label>
          <Input
            id="account-name"
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
            placeholder="Work GitHub"
            required
          />
        </div>
        {pluginId === "github" ? (
          <div className="space-y-2">
            <Label htmlFor="account-repositories">Allowed repositories</Label>
            <Input
              id="account-repositories"
              value={repository}
              onChange={(event) => setRepository(event.target.value)}
              placeholder="owner/sandbox"
              required
            />
          </div>
        ) : null}
        {pluginId === "regina-maria" ? (
          <div className="space-y-2">
            <Label htmlFor="rm-connector">Authorized profile</Label>
            <select
              id="rm-connector"
              className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm"
              value={connectorId}
              onChange={(event) => setConnectorId(event.target.value)}
              required
            >
              <option value="">Select profile</option>
              {(rmConnectors.data?.items ?? []).map((connector) => (
                <option key={connector.id} value={connector.id}>
                  {connector.displayName}
                </option>
              ))}
            </select>
          </div>
        ) : null}
        {pluginId !== "gmail" && pluginId !== "regina-maria" ? (
          <div className="space-y-2">
            <Label htmlFor="account-secret">Fine-grained token</Label>
            <Input
              id="account-secret"
              type="password"
              autoComplete="off"
              value={secret}
              onChange={(event) => setSecret(event.target.value)}
              required
            />
          </div>
        ) : null}
        <div className="md:col-span-2">
          <Button
            disabled={
              connect.isPending || (pluginId === "regina-maria" && !connectorId)
            }
          >
            <KeyRound aria-hidden />
            {pluginId === "gmail"
              ? "Continue with Google"
              : pluginId === "regina-maria"
                ? "Connect authorized profile"
                : "Connect and validate"}
          </Button>
          <p className="mt-2 text-xs text-muted-foreground">
            {pluginId === "gmail"
              ? "Google authorization opens in a separate window. Tessera stores the resulting refresh credential in custody and never returns it through the product API."
              : pluginId === "regina-maria"
                ? "Tessera verifies the selected isolated Regina Maria session and main-profile identity. No password or session cookie enters the product database."
                : "The token is sent directly to credential custody and is never returned by the product API."}
          </p>
        </div>
      </form>
      {problem(connect.error) ? (
        <Alert variant="destructive" className="mt-4">
          <AlertDescription>{problem(connect.error)}</AlertDescription>
        </Alert>
      ) : null}
      {items.length === 0 ? (
        <p className="py-10 text-center text-sm text-muted-foreground">
          Connect an account to make its capabilities available.
        </p>
      ) : (
        <ul className="divide-y divide-border">
          {items.map((account) => (
            <li key={account.id} className="py-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <p className="font-medium">{account.displayName}</p>
                  <p className="text-sm text-muted-foreground">
                    {account.providerId} ·{" "}
                    {account.identityHint ??
                      "Identity appears after validation"}
                    {account.providerAccountId
                      ? ` · provider ID ${account.providerAccountId}`
                      : ""}
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  <ProductStateBadge state={account.lifecycle} />
                  <ProductStateBadge state={account.health} />
                </div>
              </div>
              {account.lifecycle === "AUTH_REQUIRED" ||
              account.lifecycle === "DEGRADED" ? (
                <Alert className="mt-3">
                  <AlertDescription>
                    {account.lifecycle === "AUTH_REQUIRED"
                      ? account.providerId === "regina-maria"
                        ? "Authorization required. The account holder must complete the secure Regina Maria sign-in checkpoint for this isolated profile. Tessera will reconnect after identity verification."
                        : "The credential was rejected. Revoke this Account and connect it again with a current credential."
                      : "The provider could not be reached or returned an invalid response. Tessera preserved the Account; test it again when the provider is available."}
                  </AlertDescription>
                </Alert>
              ) : null}
              <div className="mt-3 flex flex-wrap gap-2">
                {account.lifecycle !== "REVOKED" &&
                (account.providerId !== "regina-maria" ||
                  account.lifecycle === "DISABLED") ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() =>
                      lifecycle.mutate({ account, operation: "validate" })
                    }
                  >
                    {account.lifecycle === "DISABLED"
                      ? "Enable and test"
                      : "Test connection"}
                  </Button>
                ) : null}
                {account.lifecycle !== "DISABLED" &&
                account.lifecycle !== "REVOKED" ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => setDisable(account)}
                  >
                    Disable
                  </Button>
                ) : null}
                {account.lifecycle !== "REVOKED" ? (
                  <Button
                    size="sm"
                    variant="destructive"
                    onClick={() => setRevoke(account)}
                  >
                    Revoke
                  </Button>
                ) : null}
              </div>
              <p className="mt-2 text-xs text-muted-foreground">
                Tessera permissions: {account.permissions.join(", ") || "None"}{" "}
                · Provider-reported scopes:{" "}
                {account.providerScopes.join(", ") ||
                  "Not reported (typical for fine-grained tokens)"}{" "}
                · Capabilities: {account.capabilityIds.join(", ") || "None"}
                {account.lastSuccessfulUse
                  ? ` · Last verified ${new Date(account.lastSuccessfulUse).toLocaleString()}`
                  : ""}
              </p>
            </li>
          ))}
        </ul>
      )}
      <Dialog
        open={Boolean(disable)}
        onOpenChange={(open) => {
          if (!open) setDisable(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Disable {disable?.displayName}</DialogTitle>
            <DialogDescription>
              This blocks new Chat and Job use while preserving account history
              and credential custody. You can enable and test it again later.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDisable(null)}>
              Keep enabled
            </Button>
            <Button
              variant="destructive"
              disabled={lifecycle.isPending}
              onClick={() =>
                disable &&
                lifecycle.mutate({ account: disable, operation: "disable" })
              }
            >
              Disable account
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <Dialog
        open={Boolean(revoke)}
        onOpenChange={(open) => {
          if (!open) setRevoke(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Revoke {revoke?.displayName}</DialogTitle>
            <DialogDescription>
              Revocation immediately blocks Chat and Jobs from using this
              account. Credential cleanup may continue in the background.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRevoke(null)}>
              Keep account
            </Button>
            <Button
              variant="destructive"
              onClick={() =>
                revoke &&
                lifecycle.mutate({ account: revoke, operation: "revoke" })
              }
            >
              Revoke account
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ProductState>
  );
}

export function JobsPage() {
  const client = useQueryClient();
  const query = useQuery({
    queryKey: ["r2", "jobs"],
    queryFn: r2Api.jobs,
    refetchInterval: 5000,
  });
  const profiles = useQuery({
    queryKey: ["r2", "model-profiles"],
    queryFn: r2Api.modelProfiles,
  });
  const settings = useQuery({
    queryKey: ["r2", "settings"],
    queryFn: r2Api.settings,
  });
  const accounts = useQuery({
    queryKey: ["r2", "accounts"],
    queryFn: r2Api.accounts,
  });
  const capabilities = useQuery({
    queryKey: ["r2", "capabilities"],
    queryFn: r2Api.capabilities,
  });
  const [name, setName] = useState("");
  const [instruction, setInstruction] = useState("");
  const [scheduleKind, setScheduleKind] = useState<
    "once" | "daily" | "weekday"
  >("once");
  const [at, setAt] = useState("");
  const [localTime, setLocalTime] = useState("08:00");
  const [accountIds, setAccountIds] = useState<string[]>([]);
  const [selected, setSelected] = useState<R2Job | null>(null);
  const [cancelJob, setCancelJob] = useState<R2Job | null>(null);
  const [selectedRun, setSelectedRun] = useState<string | null>(null);
  const runs = useQuery({
    queryKey: ["r2", "job-runs", selected?.id],
    queryFn: () => r2Api.jobRuns(selected!.id),
    enabled: Boolean(selected),
    refetchInterval: selected ? 2000 : false,
  });
  const runDetail = useQuery({
    queryKey: ["r2", "job-run", selectedRun],
    queryFn: () => r2Api.jobRun(selectedRun!),
    enabled: Boolean(selectedRun),
    refetchInterval: selectedRun ? 2000 : false,
  });
  const readCapabilities = new Set(
    (capabilities.data?.items ?? [])
      .filter((item) => item.available && item.sideEffectClass === "ReadOnly")
      .map((item) => item.id),
  );
  const integrationAccounts = (accounts.data?.items ?? []).filter(
    (item) =>
      item.lifecycle === "CONNECTED" &&
      item.capabilityIds.some((id) => readCapabilities.has(id)),
  );
  const create = useMutation({
    mutationFn: () => {
      const enabled = profiles.data?.items.filter((item) => item.enabled) ?? [];
      const profile =
        enabled.find(
          (item) => item.profileId === settings.data?.defaultChatModelProfileId,
        ) ?? (enabled.length === 1 ? enabled[0] : undefined);
      if (!profile)
        throw new Error(
          enabled.length > 1
            ? "Choose a default model in Settings before creating a Job."
            : "Configure a model before creating a Job.",
        );
      const selectedAccounts = integrationAccounts.filter((item) =>
        accountIds.includes(item.id),
      );
      const capabilityIds = new Set<string>(["model.chat.complete"]);
      for (const account of selectedAccounts)
        for (const id of account.capabilityIds)
          if (readCapabilities.has(id)) capabilityIds.add(id);
      const schedule =
        scheduleKind === "once"
          ? {
              kind: "once" as const,
              at: (() => {
                const scheduled = new Date(at);
                if (!at || Number.isNaN(scheduled.valueOf()) || scheduled <= new Date())
                  throw new Error("Choose a future date and time for this Job.");
                return scheduled.toISOString();
              })(),
              localTime: null,
              timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
              days: null,
            }
          : {
              kind: scheduleKind,
              at: null,
              localTime,
              timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
              days: scheduleKind === "weekday" ? [1, 2, 3, 4, 5] : null,
            };
      return r2Api.createJob({
        name,
        instruction,
        desiredState: "ACTIVE",
        modelProfileId: profile.profileId,
        schedule,
        contextPolicy: { includeMemory: true, includeFollowUps: true },
        accountGrants: [
          profile.accountId,
          ...selectedAccounts.map((item) => item.id),
        ],
        capabilityGrants: [...capabilityIds].map((id) => ({
          id,
          version: "1",
        })),
        sideEffectGrants: [],
      });
    },
    onSuccess: () => {
      setName("");
      setInstruction("");
      setAccountIds([]);
      void client.invalidateQueries({ queryKey: ["r2", "jobs"] });
    },
  });
  const mutate = useMutation<
    unknown,
    Error,
    { job: R2Job; operation: "run" | "pause" | "resume" | "cancel" }
  >({
    mutationFn: ({ job, operation }) =>
      operation === "run"
        ? r2Api.runJob(job)
        : operation === "cancel"
          ? r2Api.cancelJob(job)
          : r2Api.setJobState(job, operation),
    onSuccess: () => {
      setCancelJob(null);
      void client.invalidateQueries({ queryKey: ["r2", "jobs"] });
      void client.invalidateQueries({ queryKey: ["r2", "job-runs"] });
    },
  });
  const items = query.data?.items ?? [];
  return (
    <ProductState
      title="Jobs"
      description="Durable scheduled instructions with explicit account, capability, and context grants."
      empty="No Jobs yet"
      icon={CalendarClock}
      loading={query.isLoading}
      error={query.error}
    >
      <form
        className="grid gap-3 border-b border-border py-5 md:grid-cols-2"
        onSubmit={(event) => {
          event.preventDefault();
          create.mutate();
        }}
      >
        <div className="space-y-2">
          <Label htmlFor="job-name">Name</Label>
          <Input
            id="job-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="job-schedule">Schedule</Label>
          <select
            id="job-schedule"
            className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm"
            value={scheduleKind}
            onChange={(event) =>
              setScheduleKind(event.target.value as typeof scheduleKind)
            }
          >
            <option value="once">One time</option>
            <option value="daily">Daily</option>
            <option value="weekday">Weekdays</option>
          </select>
        </div>
        <div className="space-y-2 md:col-span-2">
          <Label htmlFor="job-instruction">Instruction</Label>
          <Input
            id="job-instruction"
            value={instruction}
            onChange={(event) => setInstruction(event.target.value)}
            required
          />
        </div>
        {integrationAccounts.length ? (
          <fieldset className="space-y-2 md:col-span-2">
            <legend className="text-sm font-medium">
              Integration accounts
            </legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {integrationAccounts.map((account) => (
                <label
                  key={account.id}
                  className="flex items-center gap-2 text-sm"
                >
                  <input
                    type="checkbox"
                    checked={accountIds.includes(account.id)}
                    onChange={(event) =>
                      setAccountIds((current) =>
                        event.target.checked
                          ? [...current, account.id]
                          : current.filter((id) => id !== account.id),
                      )
                    }
                  />
                  {account.displayName}
                </label>
              ))}
            </div>
            <p className="text-xs text-muted-foreground">
              Selected accounts contribute only their currently available read
              capabilities. Review or change the exact grants in Job access.
            </p>
          </fieldset>
        ) : null}
        <div className="space-y-2">
          <Label htmlFor="job-time">
            {scheduleKind === "once" ? "Run at" : "Local time"}
          </Label>
          <Input
            id="job-time"
            type={scheduleKind === "once" ? "datetime-local" : "time"}
            value={scheduleKind === "once" ? at : localTime}
            onChange={(event) =>
              scheduleKind === "once"
                ? setAt(event.target.value)
                : setLocalTime(event.target.value)
            }
            required
          />
        </div>
        <div className="flex items-end">
          <Button disabled={create.isPending}>Review and create Job</Button>
        </div>
      </form>
      {problem(create.error) ? (
        <Alert variant="destructive" className="mt-4">
          <AlertDescription>{problem(create.error)}</AlertDescription>
        </Alert>
      ) : null}
      {items.length === 0 ? (
        <p className="py-10 text-center text-sm text-muted-foreground">
          Create an instruction Tessera can run later or repeatedly.
        </p>
      ) : (
        <ul className="divide-y divide-border">
          {items.map((job) => (
            <li key={job.id} className="py-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <button className="text-left" onClick={() => setSelected(job)}>
                  <span className="font-medium">{job.name}</span>
                  <span className="mt-1 block text-sm text-muted-foreground">
                    {job.instruction}
                  </span>
                </button>
                <div className="flex gap-2">
                  <ProductStateBadge state={job.health} />
                  <ProductStateBadge state={job.desiredState} />
                </div>
              </div>
              <p className="mt-2 text-xs text-muted-foreground">
                Next:{" "}
                {job.nextOccurrence
                  ? new Date(job.nextOccurrence).toLocaleString()
                  : "No next run"}{" "}
                · Accounts {job.accountGrants.length} · Capabilities{" "}
                {job.capabilityGrants.length}
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => mutate.mutate({ job, operation: "run" })}
                >
                  Run now
                </Button>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() =>
                    mutate.mutate({
                      job,
                      operation:
                        job.desiredState === "PAUSED" ? "resume" : "pause",
                    })
                  }
                >
                  {job.desiredState === "PAUSED" ? "Resume" : "Pause"}
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => setSelected(job)}
                >
                  History
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => setCancelJob(job)}
                >
                  Cancel
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
      <Dialog
        open={Boolean(selected)}
        onOpenChange={(open) => {
          if (!open) {
            setSelected(null);
            setSelectedRun(null);
          }
        }}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>{selected?.name}</DialogTitle>
            <DialogDescription>{selected?.instruction}</DialogDescription>
          </DialogHeader>
          {runs.data?.items.map((run) => (
            <button
              key={run.id}
              className="flex w-full items-center justify-between border-b border-border py-3 text-left"
              onClick={() => setSelectedRun(run.id)}
            >
              <span>{new Date(run.scheduledFor).toLocaleString()}</span>
              <ProductStateBadge state={run.state} />
            </button>
          ))}
          {runDetail.data ? <JobRunTimeline detail={runDetail.data} /> : null}
        </DialogContent>
      </Dialog>
      <Dialog
        open={Boolean(cancelJob)}
        onOpenChange={(open) => {
          if (!open) setCancelJob(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cancel {cancelJob?.name}</DialogTitle>
            <DialogDescription>
              Tessera will stop future runs. Existing run history, Actions,
              outputs, and Evidence remain available.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setCancelJob(null)}>
              Keep Job
            </Button>
            <Button
              variant="destructive"
              disabled={!cancelJob || mutate.isPending}
              onClick={() =>
                cancelJob && mutate.mutate({ job: cancelJob, operation: "cancel" })
              }
            >
              Cancel Job
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ProductState>
  );
}

export function PluginsPage() {
  const client = useQueryClient();
  const [remove, setRemove] = useState<R2Plugin | null>(null);
  const [submittedSearch, setSubmittedSearch] = useState("");
  const query = useQuery({
    queryKey: ["r2", "plugins"],
    queryFn: r2Api.plugins,
  });
  const capabilities = useQuery({
    queryKey: ["r2", "capabilities"],
    queryFn: r2Api.capabilities,
  });
  const sources = useQuery({
    queryKey: ["r2", "integration-sources"],
    queryFn: r2Api.integrationSources,
  });
  const search = useQuery({
    queryKey: ["r2", "integration-search", submittedSearch],
    queryFn: () => r2Api.searchIntegrations(submittedSearch),
    enabled: submittedSearch.length >= 2,
  });
  const mutation = useMutation({
    mutationFn: ({
      plugin,
      operation,
    }: {
      plugin: R2Plugin;
      operation: "toggle" | "remove";
    }) =>
      operation === "toggle"
        ? r2Api.setPluginEnabled(plugin)
        : r2Api.removePlugin(plugin),
    onSuccess: () => {
      setRemove(null);
      void client.invalidateQueries({ queryKey: ["r2", "plugins"] });
      void client.invalidateQueries({ queryKey: ["r2", "capabilities"] });
    },
  });
  const install = useMutation({
    mutationFn: r2Api.installReviewedIntegration,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["r2", "plugins"] });
      void client.invalidateQueries({ queryKey: ["r2", "capabilities"] });
      void client.invalidateQueries({ queryKey: ["r2", "integration-search"] });
      void client.invalidateQueries({ queryKey: ["r2", "setup"] });
    },
  });
  const items = query.data?.items ?? [];
  return (
    <ProductState
      title="Plugins"
      description="Validated trusted-local integrations. Repository access is configured on each Account, where Tessera enforces it."
      empty="No trusted plugins are installed"
      icon={Blocks}
      loading={query.isLoading || capabilities.isLoading}
      error={query.error ?? capabilities.error}
    >
      {items.length ? (
        <ul className="divide-y divide-border">
          {items.map((plugin) => {
            const pluginCapabilities = (capabilities.data?.items ?? []).filter(
              (item) => item.pluginId === plugin.id,
            );
            const ready =
              plugin.enabled &&
              pluginCapabilities.every((item) => item.available);
            return (
              <li key={`${plugin.id}@${plugin.version}`} className="py-4">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <p className="font-medium">{plugin.name}</p>
                    <p className="text-sm text-muted-foreground">
                      Installed {plugin.id}@{plugin.version} ·{" "}
                      {plugin.publisher}
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <ProductStateBadge state="INSTALLED" />
                    <ProductStateBadge
                      state={plugin.enabled ? "ENABLED" : "DISABLED"}
                    />
                    <ProductStateBadge state={plugin.configurationState} />
                    <ProductStateBadge state={ready ? "READY" : "BLOCKED"} />
                  </div>
                </div>
                <ul className="mt-3 space-y-2">
                  {pluginCapabilities.map((capability) => (
                    <li
                      key={`${capability.id}@${capability.version}`}
                      className="flex items-center justify-between gap-3 text-sm"
                    >
                      <span>
                        {capability.description}
                        <span className="ml-2 text-xs text-muted-foreground">
                          {capability.sideEffectClass}
                        </span>
                      </span>
                      {capability.available ? (
                        <Badge variant="outline">Available</Badge>
                      ) : (
                        <Badge variant="outline">
                          {capability.blockedCode?.replaceAll("_", " ")}
                        </Badge>
                      )}
                    </li>
                  ))}
                </ul>
                <div className="mt-3 flex gap-2">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() =>
                      mutation.mutate({ plugin, operation: "toggle" })
                    }
                  >
                    {plugin.enabled ? "Disable" : "Enable"}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => setRemove(plugin)}
                  >
                    Remove
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      ) : null}
      <IntegrationSearchPanel
        query={submittedSearch}
        items={search.data?.items ?? []}
        sources={search.data?.sources ?? sources.data?.items ?? []}
        loading={search.isFetching}
        installingId={install.isPending ? install.variables?.id ?? null : null}
        errorMessage={problem(search.error) ?? problem(install.error)}
        onSearch={setSubmittedSearch}
        onInspect={(url) => void openTrustedExternal(url)}
        onInstall={(item) => install.mutate(item)}
      />
      {problem(mutation.error) ? (
        <Alert variant="destructive" className="mt-4">
          <AlertDescription>{problem(mutation.error)}</AlertDescription>
        </Alert>
      ) : null}
      <Dialog
        open={Boolean(remove)}
        onOpenChange={(open) => {
          if (!open) setRemove(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Remove {remove?.name}</DialogTitle>
            <DialogDescription>
              Removal hides this integration and prevents it from being enabled
              again. Historical Actions and Evidence remain. Tessera refuses
              removal while an Account, Job, or unfinished Action still depends
              on it.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRemove(null)}>
              Keep plugin
            </Button>
            <Button
              variant="destructive"
              disabled={!remove || mutation.isPending}
              onClick={() =>
                remove &&
                mutation.mutate({ plugin: remove, operation: "remove" })
              }
            >
              Remove plugin
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ProductState>
  );
}

export function MemoryPage() {
  const client = useQueryClient();
  const [queryText, setQueryText] = useState("");
  const query = useQuery({
    queryKey: ["r2", "memory", queryText],
    queryFn: () =>
      r2Api.memory(queryText ? `?query=${encodeURIComponent(queryText)}` : ""),
  });
  const [value, setValue] = useState("");
  const [selected, setSelected] = useState<R2Memory | null>(null);
  const [corrected, setCorrected] = useState("");
  const why = useQuery({
    queryKey: ["r2", "memory-why", selected?.assertionId],
    queryFn: () => r2Api.memoryWhy(selected!.assertionId),
    enabled: Boolean(selected),
  });
  const history = useQuery({
    queryKey: ["r2", "memory-history", selected?.assertionId],
    queryFn: () => r2Api.memoryHistory(selected!.assertionId),
    enabled: Boolean(selected),
  });
  const refresh = () =>
    client.invalidateQueries({ queryKey: ["r2", "memory"] });
  const remember = useMutation({
    mutationFn: () => r2Api.remember("user", "explicit.note", value),
    onSuccess: () => {
      setValue("");
      void refresh();
    },
  });
  const decide = useMutation({
    mutationFn: ({
      item,
      operation,
    }: {
      item: R2Memory;
      operation: "correct" | "stop";
    }) =>
      operation === "correct"
        ? r2Api.correctMemory(item, corrected)
        : r2Api.stopUsingMemory(item),
    onSuccess: () => {
      setSelected(null);
      setCorrected("");
      void refresh();
    },
  });
  const items = query.data?.items ?? [];
  return (
    <ProductState
      title="Memory"
      description="Explicit durable state is separate from conversation history. Inspect why, correct it, or stop using it in context."
      empty="Nothing has been explicitly remembered"
      icon={Brain}
      loading={query.isLoading}
      error={query.error}
    >
      <div className="grid gap-3 border-b border-border py-5 md:grid-cols-[1fr_2fr_auto]">
        <Input
          aria-label="Search memory"
          placeholder="Search memory"
          value={queryText}
          onChange={(event) => setQueryText(event.target.value)}
        />
        <Input
          aria-label="Remember this"
          placeholder="Remember this"
          value={value}
          onChange={(event) => setValue(event.target.value)}
        />
        <Button
          disabled={!value.trim() || remember.isPending}
          onClick={() => remember.mutate()}
        >
          Remember
        </Button>
      </div>
      {items.length ? (
        <dl className="divide-y divide-border">
          {items.map((item) => (
            <div
              key={item.assertionId}
              className="flex items-start justify-between gap-3 py-4"
            >
              <div>
                <dt className="text-sm text-muted-foreground">
                  {item.subjectKey} · {item.predicate}
                </dt>
                <dd className="mt-1 font-medium">{item.value}</dd>
                <p className="mt-1 text-xs text-muted-foreground">
                  {item.status} · {new Date(item.validFrom).toLocaleString()}
                </p>
              </div>
              <Button
                size="sm"
                variant="outline"
                onClick={() => {
                  setSelected(item);
                  setCorrected(item.value);
                }}
              >
                Why / Correct
              </Button>
            </div>
          ))}
        </dl>
      ) : (
        <p className="py-10 text-center text-sm text-muted-foreground">
          Tessera has no durable memory matching this view.
        </p>
      )}
      <Dialog
        open={Boolean(selected)}
        onOpenChange={(open) => {
          if (!open) setSelected(null);
        }}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Why Tessera remembers this</DialogTitle>
            <DialogDescription>
              Evidence and history are source-grounded, not generated rationale.
            </DialogDescription>
          </DialogHeader>
          {why.isLoading ? (
            <p>Loading provenance…</p>
          ) : why.error ? (
            <Alert variant="destructive">
              <AlertDescription>Provenance is unavailable.</AlertDescription>
            </Alert>
          ) : why.data ? (
            <div>
              <p className="font-medium">{why.data.current.value}</p>
              <ul className="mt-3 space-y-2 text-xs">
                {why.data.evidence.map((evidence) => (
                  <li key={evidence.evidenceId}>
                    <span className="font-mono">{evidence.evidenceId}</span>
                    <br />
                    {evidence.sourceType} ·{" "}
                    {new Date(evidence.observedAt).toLocaleString()}
                  </li>
                ))}
              </ul>
              <p className="mt-3 text-xs text-muted-foreground">
                History entries: {history.data?.items.length ?? 0}
              </p>
            </div>
          ) : null}
          <div className="space-y-2">
            <Label htmlFor="memory-correction">Corrected value</Label>
            <Input
              id="memory-correction"
              value={corrected}
              onChange={(event) => setCorrected(event.target.value)}
            />
          </div>
          <DialogFooter>
            <Button
              variant="ghost"
              onClick={() =>
                selected && decide.mutate({ item: selected, operation: "stop" })
              }
            >
              Stop using in context
            </Button>
            <Button
              onClick={() =>
                selected &&
                decide.mutate({ item: selected, operation: "correct" })
              }
            >
              Save correction
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ProductState>
  );
}

export function SettingsPage() {
  const client = useQueryClient();
  const profiles = useQuery({
    queryKey: ["r2", "model-profiles"],
    queryFn: r2Api.modelProfiles,
  });
  const settings = useQuery({
    queryKey: ["r2", "settings"],
    queryFn: r2Api.settings,
  });
  const gateways = useQuery({
    queryKey: ["r2", "model-gateways"],
    queryFn: r2Api.modelGateways,
  });
  const [endpoint, setEndpoint] = useState("operator-configured");
  const [model, setModel] = useState("");
  const [secret, setSecret] = useState("");
  const [timezone, setTimezone] = useState(
    Intl.DateTimeFormat().resolvedOptions().timeZone,
  );
  const [defaultProfileId, setDefaultProfileId] = useState("");
  const selectedGatewayId =
    gateways.data?.items.length === 1 ? gateways.data.items[0].id : "";
  const configure = useMutation({
    mutationFn: () =>
      selectedGatewayId
        ? r2Api.configureModelGateway(selectedGatewayId, model, secret)
        : r2Api.configureModel(endpoint, model, secret),
    onSuccess: async (profile) => {
      setSecret("");
      setDefaultProfileId(profile.profileId);
      await client.invalidateQueries({ queryKey: ["r2", "model-profiles"] });
      if (settings.data)
        await r2Api.updateSettings(settings.data, {
          defaultChatModelProfileId: profile.profileId,
        });
    },
  });
  const save = useMutation({
    mutationFn: () => {
      if (!isValidTimeZone(timezone))
        return Promise.reject(new Error("Choose a valid IANA timezone, for example Europe/Bucharest."));
      return settings.data
        ? r2Api.updateSettings(settings.data, {
            timezone,
            defaultChatModelProfileId:
              defaultProfileId || settings.data.defaultChatModelProfileId,
          })
        : Promise.reject(new Error("Settings unavailable"));
    },
    onSuccess: () =>
      void client.invalidateQueries({ queryKey: ["r2", "settings"] }),
  });
  const items = profiles.data?.items ?? [];
  return (
    <ProductState
      title="Settings"
      description="Model providers, defaults, timezone, approvals, memory controls, and legacy administration."
      empty="Model configuration required"
      icon={Settings2}
      loading={profiles.isLoading || settings.isLoading}
      error={profiles.error ?? settings.error}
    >
      <details open={!items.length} className="border-b border-border py-5">
        <summary className="cursor-pointer text-sm font-medium">
          {items.length ? "Advanced model connection" : "Connect an AI model"}
        </summary>
        <form
          className="mt-4 grid gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            configure.mutate();
          }}
        >
          <Input
            aria-label="Model endpoint"
            placeholder="https://provider.example/v1"
            value={endpoint}
            onChange={(event) => setEndpoint(event.target.value)}
            required
          />
          <Input
            aria-label="Model name"
            placeholder="Model name"
            value={model}
            onChange={(event) => setModel(event.target.value)}
            required
          />
          <Input
            aria-label="Provider token"
            type="password"
            autoComplete="off"
            placeholder="Provider token"
            value={secret}
            onChange={(event) => setSecret(event.target.value)}
            required
          />
          <Button disabled={configure.isPending}>Save and validate model</Button>
          <p className="text-xs text-muted-foreground">
            Remote endpoints require HTTPS. Loopback HTTP is accepted only for
            explicitly local adapters. Existing server-owned gateways are
            discovered automatically and do not require client setup.
          </p>
        </form>
      </details>
      {problem(configure.error) ? (
        <Alert variant="destructive" className="mt-4">
          <AlertDescription>{problem(configure.error)}</AlertDescription>
        </Alert>
      ) : null}
      <div className="grid gap-3 border-b border-border py-5 sm:grid-cols-[1fr_auto]">
        <div className="space-y-2">
          <Label htmlFor="timezone">Timezone</Label>
          <Input
            id="timezone"
            value={timezone}
            onChange={(event) => setTimezone(event.target.value)}
          />
        </div>
        <div className="flex items-end">
          <Button
            variant="outline"
            disabled={!isValidTimeZone(timezone) || save.isPending}
            onClick={() => save.mutate()}
          >
            Save timezone
          </Button>
        </div>
      </div>
      {problem(save.error) ? (
        <Alert variant="destructive" className="mb-4">
          <AlertDescription>{problem(save.error)}</AlertDescription>
        </Alert>
      ) : null}
      {items.length ? (
        <ul className="divide-y divide-border">
          {items.map((profile) => (
            <li key={profile.profileId} className="py-4">
              <p className="font-medium">{profile.model}</p>
              <p className="text-sm text-muted-foreground">
                {profile.adapterKind} ·{" "}
                {profile.enabled ? "Enabled" : "Disabled"} · context{" "}
                {profile.contextLimit}
              </p>
            </li>
          ))}
        </ul>
      ) : null}
      <section className="mt-6 border-t border-border pt-5">
        <h2 className="font-semibold">Administration</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Operator-only users, all connections, and observability remain under
          legacy guarded routes.
        </p>
        <div className="mt-3 flex gap-2">
          <a href="/admin/users" className="text-sm text-accent underline">
            Users
          </a>
          <a
            href="/admin/observability"
            className="text-sm text-accent underline"
          >
            Observability
          </a>
        </div>
      </section>
    </ProductState>
  );
}

export function ActivityPage() {
  const client = useQueryClient();
  const activity = useQuery({
    queryKey: ["r2", "activity"],
    queryFn: () => r2Api.activity(),
  });
  const actions = useQuery({
    queryKey: ["r2", "actions"],
    queryFn: () => r2Api.actions("?approvalRequired=true"),
  });
  const decide = useMutation({
    mutationFn: ({
      action,
      operation,
    }: {
      action: R2Action;
      operation: "approve" | "cancel";
    }) =>
      operation === "approve"
        ? r2Api.approveAction(action)
        : r2Api.cancelAction(action),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["r2", "activity"] });
      void client.invalidateQueries({ queryKey: ["r2", "actions"] });
      void client.invalidateQueries({ queryKey: ["r2", "job-runs"] });
    },
  });
  const error = activity.error ?? actions.error;
  return (
    <ProductState
      title="Activity"
      description="Approvals, actions, Job runs, memory changes, and significant evidence."
      empty="No significant activity yet"
      icon={Activity}
      loading={activity.isLoading || actions.isLoading}
      error={error}
    >
      <div>
        {(actions.data?.items ?? []).map((action) => (
          <ActionApprovalCard
            key={action.id}
            action={action}
            busy={decide.isPending}
            error={problem(decide.error)}
            onApprove={() => decide.mutate({ action, operation: "approve" })}
            onCancel={() => decide.mutate({ action, operation: "cancel" })}
          />
        ))}
      </div>
      {(activity.data?.items ?? []).length ? (
        <ol className="divide-y divide-border">
          {activity.data!.items.map((item) => (
            <li key={item.id} className="py-4">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <p className="font-medium">{item.summary}</p>
                  <p className="text-xs text-muted-foreground">
                    {item.kind} · {new Date(item.occurredAt).toLocaleString()}
                  </p>
                </div>
                {item.state ? <ProductStateBadge state={item.state} /> : null}
              </div>
            </li>
          ))}
        </ol>
      ) : (
        <p className="py-10 text-center text-sm text-muted-foreground">
          No significant activity yet.
        </p>
      )}
    </ProductState>
  );
}
