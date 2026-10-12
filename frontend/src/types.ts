export type Mode = 'keyword' | 'regex'
export type Rule = { pattern: string; mode: Mode }
export type Preset = { id: string; label: string; executables: string[]; note: string; evidence: string; manualRequired: boolean }
export type Task = { id: string; name: string; executable: string; arguments: string[]; working_directory: string | null; allow_manual_confirmation: boolean; allow_process_exit: boolean; log_path: string | null; success_log: string | null; success_log_mode: Mode; failure_log: string | null; failure_log_mode: Mode; success_log_rules: Rule[]; failure_log_rules: Rule[]; preset_id: string | null; preset_evidence: string | null; timeout_seconds: number }
export type Slot = { id: string; enabled: boolean; days: number[]; time: string }
export type Queue = { id: string; name: string; task_ids: string[]; time_slots: Slot[] }
export type Config = { schema_version: 1; queues: Queue[]; tasks: Task[]; fired_slots: string[] }
export type Runtime = { busy: boolean; activeTaskId: string | null; cooldownSeconds: number; run: { id: string; queue_name: string; state: string; items: Array<{ task_id: string; task_name: string; state: string; completion_source?: string; error_summary?: string }> } | null }
