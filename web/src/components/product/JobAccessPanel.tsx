import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { r2Api } from '../../api/r2'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { Label } from '../ui/label'

export function JobAccessPanel() {
  const client=useQueryClient()
  const jobs=useQuery({queryKey:['r2','jobs'],queryFn:r2Api.jobs})
  const accounts=useQuery({queryKey:['r2','accounts'],queryFn:r2Api.accounts})
  const capabilities=useQuery({queryKey:['r2','capabilities'],queryFn:r2Api.capabilities})
  const [jobId,setJobId]=useState('')
  const [accountIds,setAccountIds]=useState<string[]>([])
  const [capabilityIds,setCapabilityIds]=useState<string[]>([])
  const [external,setExternal]=useState(false)
  const selected=jobs.data?.items.find((job)=>job.id===jobId)
  const choose=(id:string)=>{setJobId(id);const job=jobs.data?.items.find((item)=>item.id===id);setAccountIds(job?.accountGrants??[]);setCapabilityIds(job?.capabilityGrants??[]);setExternal(job?.sideEffectGrants.includes('ExternalCommunication')??false)}
  const save=useMutation({
    mutationFn:()=>selected?r2Api.updateJob(selected,{accountGrants:accountIds,capabilityGrants:capabilityIds.map((value)=>{const [id,version]=value.split('@');return{id,version}}),sideEffectGrants:external?['ExternalCommunication']:[]}):Promise.reject(new Error('Choose a Job.')),
    onSuccess:()=>void client.invalidateQueries({queryKey:['r2','jobs']}),
  })
  if((jobs.data?.items.length??0)===0)return null
  return <section className="mt-6 border-t border-border pt-5" aria-labelledby="job-access-title">
    <h2 id="job-access-title" className="font-semibold">Job access</h2>
    <p className="mt-1 text-sm text-muted-foreground">Review exactly which accounts and capabilities a Job may use. External communication remains separately gated.</p>
    <div className="mt-4 space-y-2"><Label htmlFor="grant-job">Job</Label><select id="grant-job" className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm" value={jobId} onChange={(event)=>choose(event.target.value)}><option value="">Choose Job</option>{jobs.data!.items.map((job)=><option key={job.id} value={job.id}>{job.name}</option>)}</select></div>
    {selected?<div className="mt-4 grid gap-5 md:grid-cols-2"><fieldset><legend className="text-sm font-medium">Accounts</legend><div className="mt-2 space-y-2">{(accounts.data?.items??[]).filter((account)=>account.lifecycle==='CONNECTED').map((account)=><label key={account.id} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={accountIds.includes(account.id)} onChange={(event)=>setAccountIds((current)=>event.target.checked?[...current,account.id]:current.filter((id)=>id!==account.id))}/>{account.displayName}</label>)}</div></fieldset><fieldset><legend className="text-sm font-medium">Capabilities</legend><div className="mt-2 space-y-2">{(capabilities.data?.items??[]).filter((capability)=>capability.available).map((capability)=>{const value=`${capability.id}@${capability.version}`;return <label key={value} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={capabilityIds.includes(value)} onChange={(event)=>setCapabilityIds((current)=>event.target.checked?[...current,value]:current.filter((id)=>id!==value))}/>{capability.description}</label>})}</div></fieldset><label className="flex items-center gap-2 text-sm md:col-span-2"><input type="checkbox" checked={external} onChange={(event)=>setExternal(event.target.checked)}/>Allow reviewed external communication Actions</label><Button className="w-fit" disabled={save.isPending} onClick={()=>save.mutate()}>Save Job access</Button></div>:null}
    {save.error?<Alert variant="destructive" className="mt-3"><AlertDescription>{save.error.message}</AlertDescription></Alert>:null}
  </section>
}
