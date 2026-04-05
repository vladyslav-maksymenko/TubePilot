param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("prd", "impl", "doctor", "install-skills")]
  [string]$Action,

  [string]$Issue,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$PromptParts
)

$ErrorActionPreference = "Stop"

# TubePilot default: work against dev/nazar instead of main.
# Override per-run if needed: $env:AIWF_BASE_BRANCH="main" (or another branch).
$aiwfBaseBranch = if ($env:AIWF_BASE_BRANCH) { $env:AIWF_BASE_BRANCH } else { "dev/nazar" }
if (-not $env:AIWF_BASE_BRANCH) { $env:AIWF_BASE_BRANCH = $aiwfBaseBranch }

$aiwfRootWin = if ($env:AIWF_ROOT_WIN) { $env:AIWF_ROOT_WIN } else { "C:\Users\Lenovo\MainProjects\warp\projects\ai-coding-workflow" }
if (!(Test-Path -LiteralPath $aiwfRootWin)) { throw "ai-coding-workflow not found at '$aiwfRootWin'. Set AIWF_ROOT_WIN to its path." }
if (-not $env:USERPROFILE) { throw "USERPROFILE is not set; cannot infer CODEX skills path." }
$codexSkillsWin = Join-Path $env:USERPROFILE ".codex\\skills"

$aiwfModel = if ($env:AIWF_CODEX_MODEL) { $env:AIWF_CODEX_MODEL } else { "gpt-5.2" }
$enforceCleanRoot = if ($env:AIWF_ENFORCE_CLEAN_ROOT) { $env:AIWF_ENFORCE_CLEAN_ROOT } else { "1" }
$linkEnvFiles = if ($env:AIWF_LINK_ENV_FILES) { $env:AIWF_LINK_ENV_FILES } else { "1" }

$prompt = ""
if ($PromptParts) {
  $prompt = ($PromptParts -join " ").Trim()
}
if ($Issue -eq "--") { $Issue = $null }
if ($PromptParts -and $PromptParts.Count -gt 0 -and $PromptParts[0] -eq "--") {
  $PromptParts = $PromptParts | Select-Object -Skip 1
  $prompt = ($PromptParts -join " ").Trim()
}

function RequireCmd([string]$name) {
  if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
    throw "Missing command: $name"
  }
}

function GetRepoRoot() {
  $root = (& git rev-parse --show-toplevel 2>$null)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
    throw "Run this from inside the target git repo."
  }
  $root = $root.Trim()
  $lower = $root.ToLowerInvariant()
  $idx = $lower.IndexOf("\.worktrees\")
  if ($idx -ge 0) { return $root.Substring(0, $idx) }
  $idx = $lower.IndexOf("/.worktrees/")
  if ($idx -ge 0) { return $root.Substring(0, $idx) }
  return $root
}

function RequireSkills([string[]]$skillNames) {
  foreach ($name in $skillNames) {
    $skillFile = Join-Path $codexSkillsWin "$name\\SKILL.md"
    if (!(Test-Path -LiteralPath $skillFile)) {
      throw "Missing required skill: $skillFile (run scripts/aiwf.ps1 install-skills)"
    }
  }
}

function EnsureGhAuth() {
  & gh auth status | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated. Run: gh auth login"
  }
}

function EnsureCleanRoot([string]$repoRoot, [string]$baseBranch) {
  if ($enforceCleanRoot -ne "1") { return }

  $branch = (& git -C $repoRoot branch --show-current).Trim()
  if ($branch -ne $baseBranch) {
    throw "Repo root must be on $baseBranch (currently: $branch). Set AIWF_ENFORCE_CLEAN_ROOT=0 to bypass."
  }
  $dirty = (& git -C $repoRoot status --porcelain)
  if ($dirty) {
    throw "Repo root working tree is not clean. Commit/stash changes before impl."
  }
}

function FillTemplate([string]$templatePath, [string]$issueNum, [string]$worktreeRel) {
  $body = Get-Content -Raw -LiteralPath $templatePath
  $body = $body.Replace("__ISSUE_NUM__", $issueNum)
  $body = $body.Replace("__WORKTREE_REL__", $worktreeRel)
  return $body
}

function WriteAiwfPromptFile([string]$repoRoot, [string]$fileName, [string]$content) {
  $dir = Join-Path $repoRoot ".aiwf"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $path = Join-Path $dir $fileName
  Set-Content -LiteralPath $path -Value $content -Encoding UTF8
  return $path
}

function GetWorktreeForIssue([string]$repoRoot, [string]$issueNum, [string]$baseBranch) {
  $branchName = "issue-$issueNum"
  $worktreeRel = ".worktrees/$branchName"
  $worktreeDir = Join-Path $repoRoot (".worktrees\\$branchName")

  $porcelain = (& git -C $repoRoot worktree list --porcelain)
  $currentWorktree = $null
  $currentBranch = $null
  foreach ($line in $porcelain) {
    if ($line -like "worktree *") { $currentWorktree = $line.Substring(8).Trim(); continue }
    if ($line -like "branch *") { $currentBranch = $line.Substring(7).Trim(); continue }
    if ([string]::IsNullOrWhiteSpace($line)) {
      if ($currentWorktree -and $currentBranch -eq "refs/heads/$branchName") {
        $rel = if ($currentWorktree.StartsWith($repoRoot)) { $currentWorktree.Substring($repoRoot.Length).TrimStart('\', '/') } else { $currentWorktree }
        $rel = $rel.Replace('\', '/')
        return @{
          WorktreeRel = $rel
          WorktreeDir = $currentWorktree
          BranchName  = $branchName
        }
      }
      $currentWorktree = $null
      $currentBranch = $null
    }
  }
  if ($currentWorktree -and $currentBranch -eq "refs/heads/$branchName") {
    $rel = if ($currentWorktree.StartsWith($repoRoot)) { $currentWorktree.Substring($repoRoot.Length).TrimStart('\', '/') } else { $currentWorktree }
    $rel = $rel.Replace('\', '/')
    return @{
      WorktreeRel = $rel
      WorktreeDir = $currentWorktree
      BranchName  = $branchName
    }
  }

  & git -C $repoRoot worktree prune | Out-Null
  $wtRoot = Join-Path $repoRoot ".worktrees"
  New-Item -ItemType Directory -Force -Path $wtRoot | Out-Null

  try { & git -C $repoRoot fetch origin $baseBranch | Out-Null } catch { try { & git -C $repoRoot fetch origin | Out-Null } catch {} }

  & git -C $repoRoot show-ref --verify --quiet "refs/heads/$branchName"
  $hasLocalBranch = $LASTEXITCODE -eq 0
  if ($hasLocalBranch) {
    & git -C $repoRoot worktree add $worktreeDir $branchName | Out-Null
  } else {
    $baseRef = $null

    & git -C $repoRoot show-ref --verify --quiet ("refs/remotes/origin/{0}" -f $baseBranch)
    if ($LASTEXITCODE -eq 0) { $baseRef = ("origin/{0}" -f $baseBranch) }

    if (-not $baseRef) {
      & git -C $repoRoot show-ref --verify --quiet ("refs/heads/{0}" -f $baseBranch)
      if ($LASTEXITCODE -eq 0) { $baseRef = $baseBranch }
    }

    if (-not $baseRef) {
      & git -C $repoRoot show-ref --verify --quiet "refs/remotes/origin/main"
      if ($LASTEXITCODE -eq 0) { $baseRef = "origin/main" }
    }

    if (-not $baseRef) { $baseRef = "main" }

    & git -C $repoRoot worktree add -b $branchName $worktreeDir $baseRef | Out-Null
  }

  return @{
    WorktreeRel = $worktreeRel
    WorktreeDir = $worktreeDir
    BranchName  = $branchName
  }
}

function LinkEnvFiles([string]$repoRoot, [string]$worktreeDir) {
  if ($linkEnvFiles -ne "1") { return }

  $ts = Get-Date -Format "yyyyMMdd-HHmmss"
  $envFiles = Get-ChildItem -LiteralPath $repoRoot -Filter ".env*" -File -ErrorAction SilentlyContinue
  foreach ($src in $envFiles) {
    $dest = Join-Path $worktreeDir $src.Name
    if (Test-Path -LiteralPath $dest) {
      $item = Get-Item -LiteralPath $dest
      if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Move-Item -LiteralPath $dest -Destination "$dest.bak.$ts"
      } else {
        Remove-Item -LiteralPath $dest -Force
      }
    }

    try {
      New-Item -ItemType SymbolicLink -Path $dest -Target $src.FullName | Out-Null
    } catch {
      Copy-Item -LiteralPath $src.FullName -Destination $dest -Force
      Write-Warning "Symlink failed for $($src.Name); copied instead. (Enable Windows Developer Mode to allow symlinks without admin.)"
    }
  }
}

switch ($Action) {
  "install-skills" {
    $skills = @(
      "gh-issue-orchestrator",
      "gh-issue-dev",
      "gh-issue-qa",
      "gh-issue-merge",
      "prd-epic-workflow",
      "epic-decompose",
      "requirements-clarity",
      "agent-learnings"
    )

    $target = $codexSkillsWin
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($s in $skills) {
      $src = Join-Path $aiwfRootWin "skills\\$s"
      if (!(Test-Path -LiteralPath $src)) { throw "Missing skill directory: $src" }
      $dest = Join-Path $target $s
      if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force -Recurse }
      New-Item -ItemType Junction -Path $dest -Target $src | Out-Null
    }
    Write-Host "Linked $($skills.Count) skills into: $target"
    break
  }
  "doctor" {
    RequireCmd git
    RequireCmd gh
    RequireCmd codex

    RequireSkills @("prd-epic-workflow", "requirements-clarity", "gh-issue-orchestrator", "gh-issue-dev", "gh-issue-qa", "gh-issue-merge")

    try {
      $repoRoot = GetRepoRoot
      Write-Host "OK repo: $repoRoot"
    } catch {
      Write-Host "WARN repo: not in a git repo (that's fine if you're just installing skills)"
    }

    Write-Host "OK aiwf root: $aiwfRootWin"
    Write-Host "OK codex skills: $codexSkillsWin"
    Write-Host "OK base branch: $aiwfBaseBranch"
    break
  }
  "prd" {
    RequireCmd git
    RequireCmd codex
    RequireSkills @("prd-epic-workflow", "requirements-clarity")

    if ([string]::IsNullOrWhiteSpace($prompt)) { throw "Usage: scripts/aiwf.ps1 prd [-Issue <epicIssue>] <prompt>" }
    $repoRoot = GetRepoRoot

    $promptFile = Join-Path $aiwfRootWin "prompts\\prd-agent.md"
    if (!(Test-Path -LiteralPath $promptFile)) { throw "Missing prompt template: $promptFile" }

    $template = Get-Content -Raw -LiteralPath $promptFile
    if (-not [string]::IsNullOrWhiteSpace($Issue)) {
      RequireCmd gh
      EnsureGhAuth

      $issueJson = (& gh issue view $Issue --json title,body,labels,comments,updatedAt) | Out-String
      if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($issueJson | Out-String).Trim())) {
        throw "Failed to load EPIC issue #$Issue. If auth just failed, run: gh auth login"
      }

      $issueCtx = @"
EPIC issue: #$Issue

Current issue JSON (source of truth; use this even if gh is unavailable inside the agent sandbox):
$issueJson

Task: rewrite the EPIC issue body to be decision-complete PRD/EPIC (single source of truth).
"@.Trim()

      $finalPrompt = "$issueCtx`n`n$prompt`n`n$template"
    } else {
      $finalPrompt = "$prompt`n`n$template"
    }

    Push-Location $repoRoot
    try {
      $finalPrompt = [string]$finalPrompt
      $promptPath = WriteAiwfPromptFile $repoRoot "prd_init.txt" $finalPrompt
      $promptRel = ".aiwf/prd_init.txt"
      $startPrompt = "Open and follow the instructions in $promptRel (read the whole file first)."

      $codexArgs = @(
        "--search",
        "--enable", "multi_agent",
        "--enable", "collaboration_modes",
        "--sandbox", "workspace-write",
        "--ask-for-approval", "on-request",
        "-m", $aiwfModel,
        "-c", 'model_reasoning_effort="high"',
        $startPrompt
      )
      & codex @codexArgs
    } finally {
      Pop-Location
    }
    break
  }
  "impl" {
    RequireCmd git
    RequireCmd gh
    RequireCmd codex
    RequireSkills @("gh-issue-orchestrator", "gh-issue-dev", "gh-issue-qa", "gh-issue-merge")

    if ([string]::IsNullOrWhiteSpace($Issue)) { throw "Usage: scripts/aiwf.ps1 impl -Issue <issueNumber> [optional prompt]" }

    EnsureGhAuth

    $repoRoot = GetRepoRoot
    EnsureCleanRoot $repoRoot $aiwfBaseBranch

    $wt = GetWorktreeForIssue $repoRoot $Issue $aiwfBaseBranch
    $worktreeRel = $wt.WorktreeRel
    $worktreeDir = $wt.WorktreeDir

    LinkEnvFiles $repoRoot $worktreeDir

    $promptFile = Join-Path $aiwfRootWin "prompts\\impl-orchestrator.md"
    if (!(Test-Path -LiteralPath $promptFile)) { throw "Missing prompt template: $promptFile" }

    $orchestratorPrompt = FillTemplate $promptFile $Issue $worktreeRel
    $branchRule = @"
Repo policy: the integration/base branch for PRs and merges is '$aiwfBaseBranch' (NOT main).
- Worktrees/issue branches should be based on '$aiwfBaseBranch'.
- PRs must target '$aiwfBaseBranch'.
"@.Trim()

    $finalPrompt = if ([string]::IsNullOrWhiteSpace($prompt)) { "$branchRule`n`n$orchestratorPrompt" } else { "$branchRule`n`n$prompt`n`n$orchestratorPrompt" }

    Push-Location $repoRoot
    try {
      $finalPrompt = [string]$finalPrompt
      $promptPath = WriteAiwfPromptFile $repoRoot ("impl_init_issue_{0}.txt" -f $Issue) $finalPrompt
      $promptRel = (".aiwf/impl_init_issue_{0}.txt" -f $Issue)
      $startPrompt = "Open and follow the instructions in $promptRel (read the whole file first)."

      $codexArgs = @(
        "--search",
        "--enable", "multi_agent",
        "--enable", "collaboration_modes",
        "--dangerously-bypass-approvals-and-sandbox",
        "-m", $aiwfModel,
        "-c", 'model_reasoning_effort="high"',
        "-C", $worktreeDir,
        $startPrompt
      )
      & codex @codexArgs
    } finally {
      Pop-Location
    }
    break
  }
}
