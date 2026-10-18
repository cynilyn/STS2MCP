# 시뮬레이터(sts2-cli) ↔ STS2MCP 관측 대응표

봇 인코더(`sts2_bot/rl/encoding.py`)가 읽는 시뮬레이터 필드를 MCP 모드가 어떻게 주는지 정리한다.
어댑터는 `sts2_bot/rl/mcp_adapter.py`(`combat_state()`), 시뮬레이터는 `sts2-cli/src/Sts2Headless/RunSimulator.cs`(`CombatPlayState`).

표기: **동일** = 같은 게임 API로 같은 값 / **근사** = 어댑터가 다른 필드에서 추정 / **없음** = 모드가 주지 않음.
"(이 브랜치)" = `feat/sim-parity-stats`에서 모드에 추가한 필드(어댑터는 아직 안 씀).

## 손패 카드

| 시뮬레이터 필드 | 인코더 사용처 | 모드 | 상태 |
|---|---|---|---|
| `id` (`CARD.X`) | 카드 임베딩 | `id` = `Id.Entry`, 어댑터가 `CARD.` 접두 | 동일 |
| `type`, `target_type` | card_features, 마스크 | 같은 enum 문자열 | 동일 |
| `cost` (int, `GetResolved()`) | f[0] 비용, f[13] X 여부, 마스크 | `cost`는 문자열("X" 포함). 어댑터가 숫자로 바꾸고 "X"는 문자열로 둠 | **다름**: 시뮬레이터는 X 카드도 int(포착된 X, 손패에선 보통 0)라 is_x=0, 어댑터는 is_x=1. → `energy_cost`(이 브랜치)로 해소 |
| `can_play` | f[1], 마스크, emax | 예전: `unplayableReason == None` | 동일(이 브랜치에서 `CanPlay()` 반환값 + 시뮬레이터의 두 덮어쓰기(살아 있는 적 없음, 별 부족)로 맞춤) |
| `keywords` (CardKeyword 이름) | f[9..12] | 현지화된 hover tip 이름 → 어댑터가 card_keywords 표로 역변환 | 근사(표에 없는 이름은 누락). → `card_keywords`(이 브랜치) |
| `upgraded` | v4 효과 벡터 | `is_upgraded` → 어댑터가 `upgraded`로 | 동일 |
| `rarity` | v4 효과 벡터 | 모드는 보냄, **어댑터가 안 옮김** | 어댑터 누락(v4만 영향) |
| `stats` {var: PreviewValue} | f[6..7], hand_stats(37+2), v4 | 예전: 설명 문장 정규식(damage/block/cards/취약/약화/힘/hploss 7개) | 근사 → `stats`(이 브랜치, 시뮬레이터와 같은 계산) |
| `damage_by_target` | hand_target_dmg, hand_kill, enemy_max_dmg, reflect | 예전: 설명 피해 × 취약 1.5, 타수는 문구 정규식 | 근사(취약 외 보정·종이 개구리·Slow·해체·calculateddamage 미반영) → `damage_by_target`(이 브랜치) |
| `description` | `_card_hits` 폴백(대상 행 없는 무작위 공격의 타수) | 게임 언어로 서식 적용된 문장. 시뮬레이터는 영문 원문 | 근사(한국어면 `_TWICE_RE` 불일치, 무작위 대상 공격의 반사 피해만 영향) |

## 적

| 시뮬레이터 필드 | 인코더 사용처 | 모드 | 상태 |
|---|---|---|---|
| 순서·`index` | 슬롯, target_index | 살아 있는 적만, 같은 순서 | 동일 |
| `id` | 적 임베딩 | `entity_id`에서 `_N` 제거 | 동일 |
| `hp`, `max_hp`, `block` | enemy_features, hand_kill | 같은 필드 | 동일 |
| `intents[].damage/hits/total_damage` | enemy f[5], threat, turn_obs | 예전: `label` 문자열("12x2")을 정규식으로(어댑터가 `type == "Attack"`만 파싱 → DeathBlow 피해 누락) | 동일 → `damage`/`hits`/`total_damage`(이 브랜치, sts2-cli와 같은 키·계산, DeathBlow 포함). 어댑터가 그대로 넘기면 됨 |
| `intends_attack` | enemy f[4] | Attack 의도 유무로 추정 | 근사(`Monster.IntendsToAttack`, DeathBlow 포함 여부 다름) |
| `powers[].id/amount` | enemy_pw_ids/amt, 반사, 측면 | `status`: `IsVisible`만, `amount` = `DisplayAmount`, `raw_amount` = `Amount`(이 브랜치) | `amount`는 `DisplayAmount`를 재정의한 파워 13종(SLOW는 Amount×10, HARDENED_SHELL은 남은 양 등)에서 다름 → `raw_amount`를 쓰면 수치 동일. 남은 차이는 숨은 파워 AMBERGRIS 1종(목록에서 빠짐) |
| `side` | turn_obs | 없음 | 인코더가 BACK_ATTACK_* 파워로 대신 판정 → 동일 효과 |

## 플레이어·전투

| 시뮬레이터 필드 | 인코더 사용처 | 모드 | 상태 |
|---|---|---|---|
| `player.hp/max_hp/block` | player_features, threat | 같은 필드 | 동일 |
| `energy`, `max_energy` | player f[4..5], 마스크 | 같은 `PlayerCombatState` | 동일 |
| `round` | player f[6] | `battle.round` = `RoundNumber` | 동일 |
| `draw/discard_pile_count` | player f[7..8] | 같은 필드 | 동일 |
| `draw/discard/exhaust_pile` (정렬 ID, "+"=강화) | 더미 장수 벡터, v4 더미 효과 | 예전: 현지화 이름만 → 이름→ID 역변환(동명 카드는 캐릭터 접미사로 추정) | 근사 → 더미 카드에 `id`·`is_upgraded`(이 브랜치). 어댑터 `card_ids`가 이미 `id`를 우선 사용 |
| `player_powers` | player_pw_ids/amt, turn_obs(SURROUNDED) | `status` (보이는 것만, `amount`=DisplayAmount, `raw_amount`=Amount(이 브랜치)) | `raw_amount`로 수치 동일, 숨은 파워(AMBERGRIS)만 차이 |
| `facing` | turn_obs | 없음 | **없음**: 황제 게 전투에서 turn_obs가 항상 0 (facing 관측을 켠 모델만 영향) |
| `player.potions` (보유 전부, index=보유 순번) | potion_ids, 포션 마스크, potion_effect | 슬롯 순서, 어댑터가 `can_use_in_combat` 아닌 것 제외 | 근사(전투 불가 포션이 시뮬레이터엔 보임, 칸 순서 다를 수 있음). 효과 vars는 카탈로그의 정적 값 |
| `player.relics[].id` | 유물 multi-hot | 같은 `Id.Entry` | 동일 |
| `context.act`, `room_type` | context | `run.act`, `state_type` 매핑 | 동일 |
| `orbs[].type/passive/evoke`, `orb_slots` | orb_ids, orb_feat | `orbs[].id`("LIGHTNING_ORB")·`name`(현지화)·`passive_val`·`evoke_val` | **어댑터 불일치**: 인코더는 `type`(없으면 `name`)·`passive`/`evoke`를 읽음 → 수치는 항상 0, 어휘는 게임 언어가 영어일 때만 맞음(한국어면 0) |
| `stars` | context | 별 카운터 표시 조건이 다름(시뮬레이터: 리젠트면 항상) | 거의 동일 |
| `osty` {alive,hp,max_hp,block} | context | 모드는 `pets[]`(Osty)로 보냄, 어댑터는 `osty`만 복사 | **어댑터 불일치**: 네크로바인더 Osty 특징 전부 0 |
| `min_select`/`max_select` (card_select) | 선택 마스크, player f[11] | hand_select에 없음 → 어댑터 추정 | 근사 |

## 결정에 영향이 큰 남은 차이 (우선순위)

1. **디펙트 구체 수치 0(한국어면 종류도 0)** — 어댑터에서 `type = id.removesuffix("_ORB").title()`, `passive = int(passive_val)`, `evoke = int(evoke_val)`로 옮기면 해결(모드 수정 불필요). 디펙트 전투 내내 관측이 틀림.
2. **네크로바인더 Osty 0** — 어댑터에서 `pets[0]`(id OSTY)를 `osty`로 옮기면 해결. 또는 모드가 시뮬레이터처럼 최상위 `osty`도 보냄.
3. **카드 수치·대상별 피해** — 이 브랜치의 `stats`·`damage_by_target`를 어댑터가 그대로 쓰도록 바꾸면 해소(정규식·취약 1.5 근사 제거). 처치 판정(hand_kill)과 전체 공격 가치에 직접 영향.
4. **X 비용 카드 표시(f[0], f[13])** — 어댑터가 `cost`를 `energy_cost`로 쓰면 시뮬레이터와 같아짐. 지금은 학습 때 본 적 없는 is_x=1 입력.
5. **의도 피해(DeathBlow 누락)** — 이 브랜치에서 모드가 의도에 `damage`/`hits`/`total_damage`를 직접 넣음. 어댑터가 라벨 정규식 대신 이 키를 쓰면 해소. 위협(threat) 특징에 직접 영향.
6. **파워 수치 차이** — 이 브랜치에서 모든 파워 항목에 `raw_amount`(= `Amount`) 추가. 어댑터 `_powers`가 `raw_amount`를 우선 쓰면 슬로우·단단한 껍질 등 13종이 맞춰짐. 숨은 파워(AMBERGRIS)까지 맞추려면 `IsVisible` 필터 없는 목록이 더 필요.
7. **카드 키워드** — 어댑터가 `card_keywords`를 우선 사용.
8. **rarity(v4)·potion 순서·facing·min/max_select** — 영향 작음. facing은 모드에 `SurroundedPower.Facing` 추가로 해결.

## 이 브랜치에서 모드에 추가한 필드

손패 카드(`player.hand[]`):

- `stats`: `{소문자 DynamicVar 이름: int PreviewValue}` — `ClearPreview → UpdateDynamicVarPreview(Normal, CurrentTarget) → 읽기 → ClearPreview`, 시뮬레이터와 같은 호출 순서.
- `damage_by_target`: 공격 카드(AnyEnemy/AllEnemies)만, 살아 있는 적마다 `{target_index, enemy_id, name, damage, repeat?, total_damage?}`. `MultiCreatureTargeting` 미리보기, `calculateddamage>0`이면 그 값, 타수는 `repeat` 변수 → X 비용이면 에너지 → 해체(DISMANTLE)는 대상 취약이면 2.
- `energy_cost`: `EnergyCost.GetResolved()` int.
- `card_keywords`: `CardKeyword` 이름 목록(없으면 생략).
- `can_play`: `CanPlay()` 반환값 + 살아 있는 적 없는 AnyEnemy 카드 false + 별 부족 false(시뮬레이터와 동일).
- `description`: 같은 Normal 미리보기 상태에서 읽는다(이전 요청의 ClearPreview 여부와 무관하게 일정).

더미 카드(`draw_pile`/`discard_pile`/`exhaust_pile`): `id`, `is_upgraded`.

적 의도(`battle.enemies[].intents[]`, `AttackIntent` 하위 전부: Attack·DeathBlow): `hits > 1`이면 `damage`(타당 `GetSingleDamage`)·`hits`(`Repeats`)·`total_damage`(`GetTotalDamage`), 아니면 `damage`(`GetTotalDamage`)만. 대상 목록은 sts2-cli처럼 `CombatManager.DebugOnlyGetState().PlayerCreatures`.

파워(플레이어·적·Osty `status[]`): `raw_amount` = `PowerModel.Amount`(sts2-cli `amount`와 같은 값). 기존 `amount`(= `DisplayAmount`)는 그대로.

## 게임 없이 확인하지 못한 것

- 실제 게임에서 `stats`/`damage_by_target` 값이 같은 상황의 시뮬레이터 값과 일치하는지(힘·취약·종이 개구리·해체·소용돌이로 대조 필요).
- 상태 요청 뒤 손패 카드 미리보기를 기본값으로 되돌려도 화면 표시·카드 사용에 문제가 없는지(PreviewValue는 표시 전용이고 NCard가 다음 갱신 때 다시 계산하지만, 렌더된 문구가 바뀌는 순간은 육안 확인 필요).
- 의도 라벨이 한국어 설정에서도 `12x2` 형식으로 남는지(어댑터가 새 `damage`/`hits` 키로 바꾸기 전까지의 전제).
