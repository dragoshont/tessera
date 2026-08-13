import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Archive,
  BriefcaseBusiness,
  MessageSquarePlus,
  Pencil,
  Trash2,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  r2Api,
  type R2Action,
  type R2Conversation,
} from "../api/r2";
import { ChatWorkspace } from "../components/chat/ChatWorkspace";
import { ActionApprovalCard } from "../components/product/R2ProductComponents";
import { SetupCenter } from "../components/product/SetupCenter";
import { Alert, AlertDescription } from "../components/ui/alert";
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
import { useRealtimeVoice } from "../hooks/useRealtimeVoice";

export function ChatPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string>();
  const [rename, setRename] = useState("");
  const [jobOpen, setJobOpen] = useState(false);
  const [jobName, setJobName] = useState("");
  const [jobInstruction, setJobInstruction] = useState("");
  const [integrationAccountId, setIntegrationAccountId] = useState("");
  const setupAttempted = useRef(false);
  const [localPendingExecution, setPendingExecution] = useState<{
    messageId: string;
    executionId: string;
  } | null>(null);
  const [streamingText, setStreamingText] = useState("");
  const setup = useQuery({
    queryKey: ["r2", "setup"],
    queryFn: r2Api.setupStatus,
  });
  const bootstrap = useMutation({
    mutationFn: r2Api.bootstrapSetup,
    onSuccess: (value) => {
      queryClient.setQueryData(["r2", "setup"], value);
      void queryClient.invalidateQueries({ queryKey: ["r2", "model-profiles"] });
      void queryClient.invalidateQueries({ queryKey: ["r2", "settings"] });
    },
  });
  useEffect(() => {
    if (
      setup.data?.ai.state === "READY_TO_CONNECT" &&
      !setupAttempted.current &&
      !bootstrap.isPending
    ) {
      setupAttempted.current = true;
      bootstrap.mutate();
    }
  }, [bootstrap, setup.data?.ai.state]);
  const profiles = useQuery({
    queryKey: ["r2", "model-profiles"],
    queryFn: r2Api.modelProfiles,
  });
  const settings = useQuery({
    queryKey: ["r2", "settings"],
    queryFn: r2Api.settings,
  });
  const conversations = useQuery({
    queryKey: ["r2", "conversations"],
    queryFn: r2Api.conversations,
  });
  const accounts = useQuery({
    queryKey: ["r2", "accounts"],
    queryFn: r2Api.accounts,
  });
  const capabilities = useQuery({
    queryKey: ["r2", "capabilities"],
    queryFn: r2Api.capabilities,
  });
  const enabledProfiles =
    profiles.data?.items.filter((profile) => profile.enabled) ?? [];
  const activeProfile =
    enabledProfiles.find(
      (profile) =>
        profile.profileId === settings.data?.defaultChatModelProfileId,
    ) ?? (enabledProfiles.length === 1 ? enabledProfiles[0] : undefined);
  const integrationAccounts =
    accounts.data?.items.filter(
      (item) =>
        item.lifecycle === "CONNECTED" &&
        item.id !== activeProfile?.accountId &&
        item.capabilityIds.length > 0,
    ) ?? [];
  const integrationAccount = integrationAccounts.find(
    (item) => item.id === integrationAccountId,
  );
  const activeConversation =
    conversations.data?.items.find((item) => item.id === selectedId) ??
    conversations.data?.items.find((item) => item.state === "ACTIVE");
  const voiceStatus = useQuery({
    queryKey: ["r2", "realtime-voice-status"],
    queryFn: r2Api.realtimeVoiceStatus,
    refetchInterval: (query) => query.state.data?.state === "CHECKING" ? 2000 : 5 * 60 * 1000,
  });
  const realtimeVoice = useRealtimeVoice({
    conversationId: activeConversation?.id,
    status: voiceStatus.data,
    onTurnSaved: () => {
      void queryClient.invalidateQueries({ queryKey: ["r2", "messages"] });
      void queryClient.invalidateQueries({ queryKey: ["r2", "conversations"] });
    },
    onApprovalRequired: () => {
      void queryClient.invalidateQueries({ queryKey: ["r2", "actions"] });
      void queryClient.invalidateQueries({ queryKey: ["r2", "messages"] });
    },
  });
  const activeExecution = useQuery({
    queryKey: ["r2", "active-execution", activeConversation?.conversationId],
    queryFn: () => r2Api.activeExecution(activeConversation!.conversationId),
    enabled: Boolean(activeConversation),
  });
  const pendingExecution =
    localPendingExecution ??
    (activeExecution.data
      ? {
          messageId: activeExecution.data.executionId,
          executionId: activeExecution.data.executionId,
        }
      : null);
  const watchedExecutionId =
    pendingExecution?.executionId ?? activeExecution.data?.executionId;
  const watchedMessageId =
    pendingExecution?.messageId ?? activeExecution.data?.executionId;
  const messages = useQuery({
    queryKey: ["r2", "messages", activeConversation?.conversationId],
    queryFn: () => r2Api.messages(activeConversation!.conversationId),
    enabled: Boolean(activeConversation),
    refetchInterval: (query) =>
      watchedExecutionId &&
      !query.state.data?.items.some(
        (message) => message.messageId === watchedMessageId,
      )
        ? 500
        : false,
  });
  const conversationGrants = useQuery({
    queryKey: ["r2", "conversation-grants", activeConversation?.id],
    queryFn: () => r2Api.conversationGrants(activeConversation!.id),
    enabled: Boolean(activeConversation),
  });
  const actions = useQuery({
    queryKey: ["r2", "actions", activeConversation?.id],
    queryFn: () =>
      r2Api.actions(
        `?conversationId=${encodeURIComponent(activeConversation!.id)}`,
      ),
    enabled: Boolean(activeConversation),
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ["r2", "conversations"] });
    await queryClient.invalidateQueries({ queryKey: ["r2", "messages"] });
    await queryClient.invalidateQueries({ queryKey: ["r2", "actions"] });
  };
  useEffect(() => {
    if (!watchedExecutionId || !activeConversation) return;
    const executionId = watchedExecutionId;
    const controller = new AbortController();
    void (async () => {
      try {
        await r2Api.watchExecution(
          activeConversation.id,
          executionId,
          controller.signal,
          (event) => {
            const data = event.data;
            if (
              event.type === "text" &&
              typeof data === "object" &&
              data !== null &&
              "delta" in data &&
              typeof data.delta === "string"
            ) {
              const delta = data.delta;
              setStreamingText((current) => current + delta);
            }
            if (event.type === "approval_required")
              void queryClient.invalidateQueries({
                queryKey: ["r2", "actions"],
              });
          },
        );
        await queryClient.invalidateQueries({ queryKey: ["r2", "messages"] });
        await queryClient.invalidateQueries({ queryKey: ["r2", "actions"] });
        await queryClient.invalidateQueries({
          queryKey: ["r2", "active-execution"],
        });
        setPendingExecution((current) =>
          current?.executionId === executionId ? null : current,
        );
        setStreamingText("");
      } catch (error: unknown) {
        if (!(error instanceof DOMException && error.name === "AbortError")) {
          void queryClient.invalidateQueries({ queryKey: ["r2", "messages"] });
          void queryClient.invalidateQueries({
            queryKey: ["r2", "active-execution"],
          });
          setPendingExecution((current) =>
            current?.executionId === executionId ? null : current,
          );
          setStreamingText("");
        }
      }
    })();
    return () => controller.abort();
  }, [activeConversation, queryClient, watchedExecutionId]);
  const create = useMutation({
    mutationFn: () =>
      r2Api.createConversation(activeProfile?.profileId ?? null),
    onSuccess: (item) => {
      setSelectedId(item.id);
      void refresh();
    },
  });
  const update = useMutation<
    unknown,
    Error,
    { item: R2Conversation; operation: "rename" | "archive" | "delete" }
  >({
    mutationFn: ({ item, operation }) =>
      operation === "delete"
        ? r2Api.deleteConversation(item)
        : r2Api.updateConversation(
            item,
            operation === "rename" ? { title: rename } : { state: "ARCHIVED" },
          ),
    onSuccess: () => {
      setRename("");
      setSelectedId(undefined);
      void refresh();
    },
  });
  const send = useMutation({
    mutationFn: async (text: string) => {
      if (!activeProfile) throw new Error("configuration_required");
      const conversation =
        activeConversation ??
        (await r2Api.createConversation(activeProfile.profileId));
      return r2Api.sendMessage(
        conversation.conversationId,
        activeProfile.profileId,
        text,
      );
    },
    onSuccess: (receipt) => {
      setStreamingText("");
      setPendingExecution(receipt);
      void refresh();
    },
  });
  const stop = useMutation({
    mutationFn: () =>
      watchedExecutionId && activeConversation
        ? r2Api.stopExecution(activeConversation.id, watchedExecutionId)
        : Promise.reject(new Error("No active execution.")),
    onSuccess: () => {
      setPendingExecution(null);
      void refresh();
    },
  });
  const retry = useMutation({
    mutationFn: (messageId: string) =>
      r2Api.retryMessage(activeConversation!.id, messageId),
    onSuccess: (receipt) => {
      setStreamingText("");
      setPendingExecution(receipt);
      void refresh();
    },
  });
  const grantIntegration = useMutation({
    mutationFn: () => {
      if (
        !activeConversation ||
        !conversationGrants.data ||
        !integrationAccount
      )
        throw new Error("Choose an integration account.");
      const capabilities = conversationGrants.data.capabilityGrants.map(
        (entry) => {
          const [id, version] = entry.split("@");
          return { id, version };
        },
      );
      for (const id of integrationAccount.capabilityIds)
        if (
          !capabilities.some((item) => item.id === id && item.version === "1")
        )
          capabilities.push({ id, version: "1" });
      return r2Api.updateConversationGrants(
        activeConversation.id,
        conversationGrants.data,
        [
          ...new Set([
            ...conversationGrants.data.accountGrants,
            integrationAccount.id,
          ]),
        ],
        capabilities,
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ["r2", "conversation-grants"],
      });
      void queryClient.invalidateQueries({ queryKey: ["r2", "conversations"] });
    },
  });
  const remember = useMutation({
    mutationFn: async () => {
      const message = [...(messages.data?.items ?? [])]
        .reverse()
        .find((item) => item.role === "USER");
      const text = message?.parts.find((part) => part.kind === "TEXT")?.text;
      if (!message || !text)
        throw new Error("No user message is available to remember.");
      return r2Api.remember("user", "explicit.note", text, message.id);
    },
    onSuccess: () =>
      void queryClient.invalidateQueries({ queryKey: ["r2", "memory"] }),
  });
  const job = useMutation({
    mutationFn: () => {
      if (!activeProfile)
        throw new Error("Configure a model before creating a Job.");
      return r2Api.createJob({
        name: jobName,
        instruction: jobInstruction,
        desiredState: "ACTIVE",
        modelProfileId: activeProfile.profileId,
        schedule: {
          kind: "weekday",
          at: null,
          localTime: "08:00",
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          days: [1, 2, 3, 4, 5],
        },
        contextPolicy: { includeMemory: true, includeFollowUps: true },
        accountGrants: [activeProfile.accountId],
        capabilityGrants: [{ id: "model.chat.complete", version: "1" }],
        sideEffectGrants: [],
      });
    },
    onSuccess: () => {
      setJobOpen(false);
      setJobName("");
      setJobInstruction("");
      void queryClient.invalidateQueries({ queryKey: ["r2", "jobs"] });
    },
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
    onSettled: refresh,
  });
  const invokeTime = useMutation({
    mutationFn: () => {
      if (!activeConversation) throw new Error("Create a conversation first.");
      const lastUser = messages.data?.items.findLast(
        (item) => item.role === "USER",
      );
      return r2Api.invokeCapability({
        capabilityId: "local.time",
        capabilityVersion: "1",
        pluginId: "local",
        pluginVersion: "1.0.0",
        accountId: null,
        target: "UTC",
        input: { timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone },
        conversationId: activeConversation.id,
        messageId: lastUser?.id,
      });
    },
    onSuccess: refresh,
  });
  const turns = (messages.data?.items ?? []).map((message) => ({
    id: message.messageId,
    role:
      message.role === "USER"
        ? ("user" as const)
        : message.role === "ASSISTANT"
          ? ("assistant" as const)
          : ("event" as const),
    text: message.parts
      .map(
        (part) =>
          part.text ??
          (part.errorCode ? recoveryMessage(part.errorCode) : null) ??
          (part.kind === "CAPABILITY_RESULT"
            ? `Capability result ${part.capabilityResultId}`
            : part.kind === "ACTION"
              ? `Action ${part.actionId}`
              : null),
      )
      .filter(Boolean)
      .join("\n"),
    status: message.status,
    retryable:
      message.role === "ASSISTANT" &&
      (message.status === "FAILED" || message.status === "STOPPED"),
  }));
  if (
    watchedExecutionId &&
    watchedMessageId &&
    streamingText &&
    !messages.data?.items.some(
      (message) => message.messageId === watchedMessageId,
    )
  )
    turns.push({
      id: watchedMessageId,
      role: "assistant" as const,
      text: streamingText,
      status: "STREAMING",
      retryable: false,
    });
  const error =
    profiles.error ?? conversations.error ?? messages.error ?? send.error;
  const integrationGranted = Boolean(
    integrationAccount &&
    conversationGrants.data?.accountGrants.includes(integrationAccount.id),
  );
  if (setup.isLoading)
    return (
      <div className="py-16 text-center text-sm text-muted-foreground">
        Checking Tessera…
      </div>
    );
  if (setup.data && !setup.data.canOpenChat)
    return (
      <SetupCenter
        status={setup.data}
        busy={bootstrap.isPending}
        error={bootstrap.error}
        onRetry={() => {
          setupAttempted.current = true;
          bootstrap.mutate();
        }}
        onAccounts={() => navigate("/accounts")}
      />
    );
  return (
    <div className="grid min-h-[calc(100vh-8rem)] gap-6 lg:grid-cols-[15rem_1fr]">
      <aside className="border-r border-border pr-4" aria-label="Conversations">
        <div className="flex items-center justify-between gap-2">
          <h2 className="font-semibold">Conversations</h2>
          <Button
            size="icon"
            variant="ghost"
            aria-label="New conversation"
            onClick={() => create.mutate()}
            disabled={!activeProfile}
          >
            <MessageSquarePlus aria-hidden />
          </Button>
        </div>
        <ul className="mt-3 space-y-1">
          {(conversations.data?.items ?? [])
            .filter((item) => item.state !== "DELETED")
            .map((item) => (
              <li key={item.id}>
                <button
                  className={`w-full rounded-md px-3 py-2 text-left text-sm ${item.id === activeConversation?.id ? "bg-muted font-medium" : "hover:bg-muted/60"}`}
                  onClick={() => setSelectedId(item.id)}
                >
                  {item.title}
                  <span className="block text-xs text-muted-foreground">
                    {item.state}
                  </span>
                </button>
              </li>
            ))}
        </ul>
        {activeConversation ? (
          <div className="mt-4 space-y-2">
            <Input
              aria-label="Rename conversation"
              value={rename}
              onChange={(event) => setRename(event.target.value)}
              placeholder="Rename"
            />
            <div className="flex gap-1">
              <Button
                size="icon"
                variant="ghost"
                aria-label="Save conversation name"
                disabled={!rename.trim()}
                onClick={() =>
                  update.mutate({
                    item: activeConversation,
                    operation: "rename",
                  })
                }
              >
                <Pencil aria-hidden />
              </Button>
              <Button
                size="icon"
                variant="ghost"
                aria-label="Archive conversation"
                onClick={() =>
                  update.mutate({
                    item: activeConversation,
                    operation: "archive",
                  })
                }
              >
                <Archive aria-hidden />
              </Button>
              <Button
                size="icon"
                variant="ghost"
                aria-label="Delete conversation"
                onClick={() =>
                  update.mutate({
                    item: activeConversation,
                    operation: "delete",
                  })
                }
              >
                <Trash2 aria-hidden />
              </Button>
            </div>
          </div>
        ) : null}
      </aside>
      <main>
        <ChatWorkspace
          title={activeConversation?.title}
          turns={turns}
          loading={
            profiles.isLoading || conversations.isLoading || messages.isLoading
          }
          configurationRequired={!profiles.isLoading && !activeProfile}
          errorMessage={error instanceof Error ? error.message : undefined}
          sending={
            send.isPending ||
            Boolean(
              pendingExecution &&
              !messages.data?.items.some(
                (message) => message.messageId === pendingExecution.messageId,
              ),
            )
          }
          voice={activeConversation ? realtimeVoice.voice : undefined}
          onSend={(text) => send.mutate(text)}
          onStop={() => stop.mutate()}
          onConfigure={() => navigate("/settings")}
          onRetry={(id) => retry.mutate(id)}
          onVoiceStart={() => void realtimeVoice.start()}
          onVoiceRetry={() => void realtimeVoice.retry()}
          onVoiceToggleMute={realtimeVoice.toggleMute}
          onVoiceInterrupt={realtimeVoice.interrupt}
          onVoiceEnd={() => void realtimeVoice.end()}
          onVoiceEnableAudio={realtimeVoice.enableAudio}
        />
        {activeConversation ? (
          <div className="flex flex-wrap gap-2 border-t border-border py-4">
            <Button
              variant="outline"
              onClick={() => invokeTime.mutate()}
              disabled={
                invokeTime.isPending ||
                !capabilities.data?.items.some(
                  (item) => item.id === "local.time" && item.available,
                )
              }
            >
              Current date and time
            </Button>
            <Button
              variant="outline"
              onClick={() => remember.mutate()}
              disabled={remember.isPending}
            >
              Remember last message
            </Button>
            <Button
              variant="outline"
              onClick={() => {
                setJobInstruction(
                  messages.data?.items
                    .findLast((item) => item.role === "USER")
                    ?.parts.find((part) => part.kind === "TEXT")?.text ?? "",
                );
                setJobOpen(true);
              }}
            >
              <BriefcaseBusiness aria-hidden />
              Create Job proposal
            </Button>
          </div>
        ) : null}
        {invokeTime.error ? (
          <Alert variant="destructive">
            <AlertDescription>{invokeTime.error.message}</AlertDescription>
          </Alert>
        ) : null}
        {remember.error ? (
          <Alert variant="destructive">
            <AlertDescription>{remember.error.message}</AlertDescription>
          </Alert>
        ) : null}
        {(actions.data?.items ?? []).map((action) => (
          <ActionApprovalCard
            key={action.id}
            action={action}
            busy={decide.isPending}
            error={decide.error?.message}
            accountLabel={
              accounts.data?.items.find((item) => item.id === action.accountId)
                ?.displayName
            }
            onApprove={() => decide.mutate({ action, operation: "approve" })}
            onCancel={() => decide.mutate({ action, operation: "cancel" })}
          />
        ))}
      </main>
      {activeConversation && integrationAccounts.length ? (
        <section
          className="border-t border-border py-4 lg:col-start-2"
          aria-label="Conversation integration grants"
        >
          <h2 className="text-sm font-semibold">Conversation integrations</h2>
          <div className="mt-3 flex flex-wrap gap-2">
            <select
              aria-label="Integration account for this conversation"
              className="h-10 min-w-52 rounded-md border border-border bg-card px-3 text-sm"
              value={integrationAccountId}
              onChange={(event) => setIntegrationAccountId(event.target.value)}
            >
              <option value="">Choose account</option>
              {integrationAccounts.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.displayName}
                </option>
              ))}
            </select>
            <Button
              variant="outline"
              disabled={
                !integrationAccount ||
                integrationGranted ||
                grantIntegration.isPending
              }
              onClick={() => grantIntegration.mutate()}
            >
              {integrationGranted
                ? "Integration allowed"
                : "Allow integration tools"}
            </Button>
          </div>
          <p className="mt-2 text-xs text-muted-foreground">
            Grants are conversation-scoped. Consequential capabilities still
            require exact Action approval.
          </p>
        </section>
      ) : null}
      <Dialog open={jobOpen} onOpenChange={setJobOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Review Job proposal</DialogTitle>
            <DialogDescription>
              This creates a weekday 08:00 Job with explicit model-only grants.
              Review before activation.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-3">
            <div>
              <Label htmlFor="chat-job-name">Name</Label>
              <Input
                id="chat-job-name"
                value={jobName}
                onChange={(event) => setJobName(event.target.value)}
              />
            </div>
            <div>
              <Label htmlFor="chat-job-instruction">Instruction</Label>
              <Input
                id="chat-job-instruction"
                value={jobInstruction}
                onChange={(event) => setJobInstruction(event.target.value)}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setJobOpen(false)}>
              Cancel
            </Button>
            <Button
              disabled={
                !jobName.trim() || !jobInstruction.trim() || job.isPending
              }
              onClick={() => job.mutate()}
            >
              Create active Job
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
