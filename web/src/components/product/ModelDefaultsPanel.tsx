import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { r2Api } from '../../api/r2'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { Label } from '../ui/label'

export function ModelDefaultsPanel() {
  const client=useQueryClient()
  const profiles=useQuery({queryKey:['r2','model-profiles'],queryFn:r2Api.modelProfiles})
  const settings=useQuery({queryKey:['r2','settings'],queryFn:r2Api.settings})
  const [chat,setChat]=useState('')
  const [lightweight,setLightweight]=useState('')
  const save=useMutation({
    mutationFn:()=>settings.data?r2Api.updateSettings(settings.data,{
      defaultChatModelProfileId:chat||settings.data.defaultChatModelProfileId,
      defaultLightweightModelProfileId:lightweight||settings.data.defaultLightweightModelProfileId,
    }):Promise.reject(new Error('Settings unavailable')),
    onSuccess:()=>void client.invalidateQueries({queryKey:['r2','settings']}),
  })
  const items=profiles.data?.items.filter((profile)=>profile.enabled)??[]
  if(items.length===0)return null
  return <section className="mt-6 border-t border-border pt-5" aria-labelledby="model-defaults-title">
    <h2 id="model-defaults-title" className="font-semibold">Model defaults</h2>
    <p className="mt-1 text-sm text-muted-foreground">Choose explicit profiles for Chat and lightweight background work.</p>
    <div className="mt-4 grid gap-3 sm:grid-cols-2">
      <div className="space-y-2"><Label htmlFor="default-chat-model">Chat model</Label><select id="default-chat-model" className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm" value={chat||settings.data?.defaultChatModelProfileId||''} onChange={(event)=>setChat(event.target.value)}><option value="">Choose profile</option>{items.map((profile)=><option key={profile.profileId} value={profile.profileId}>{profile.model} · {profile.adapterKind}</option>)}</select></div>
      <div className="space-y-2"><Label htmlFor="default-lightweight-model">Lightweight model</Label><select id="default-lightweight-model" className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm" value={lightweight||settings.data?.defaultLightweightModelProfileId||''} onChange={(event)=>setLightweight(event.target.value)}><option value="">Choose profile</option>{items.map((profile)=><option key={profile.profileId} value={profile.profileId}>{profile.model} · {profile.adapterKind}</option>)}</select></div>
    </div>
    <Button className="mt-4" variant="outline" disabled={save.isPending||(!chat&&!lightweight)} onClick={()=>save.mutate()}>Save model defaults</Button>
    {save.error?<Alert variant="destructive" className="mt-3"><AlertDescription>{save.error.message}</AlertDescription></Alert>:null}
  </section>
}
