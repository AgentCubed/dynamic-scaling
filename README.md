# Dynamic Scaling

A refactored version of my [original mod](https://github.com/AgentCubed/dynamic-scaling). It attempts to dynamically determine the difficulty of bosses at runtime as well as many damage configs.

## 1. Player Scaling Model

The player scaling model adjusts the damage players deal and take based on configuration, performance (deaths), and server population.

### 1.1 Damage Output Scaling ($D_{out}$)

The damage a player deals to NPCs is modified by a multiplier derived from a base configuration and player-specific overrides. Conceptually, players deal less damage as they accumulate deaths during a boss fight.

$$
\begin{aligned}
M_{deal}^{(p)} &= 1.2^{(C_{deal} - N_{deaths}^{(p)} + \delta_{deal}^{(p)})} \\
D_{final}^{(p)} &= D_{initial} \times M_{deal}^{(p)}
\end{aligned}
$$

Where:

* $C_{deal}$: Global `DealDamage` configuration value.
* $N_{deaths}^{(p)}$: Number of deaths for player $p$ during the current boss fight.
* $\delta_{deal}^{(p)}$: Player-specific `DealDamageModifierDifference` from `PlayerOverrides`.

### 1.2 Damage Intake Scaling ($D_{in}$)

The damage a player takes from NPCs is similarly modified, but deaths have a double weight to punish repeated failure more severely. Players take more damage as they accumulate deaths.

$$
\begin{aligned}
M_{take}^{(p)} &= 1.2^{(C_{take} + 2 \cdot N_{deaths}^{(p)} + \delta_{take}^{(p)})} \\
D_{taken}^{(p)} &= D_{raw} \times M_{take}^{(p)}
\end{aligned}
$$

Where:

* $C_{take}$: Global `TakeDamage` configuration value.
* $\delta_{take}^{(p)}$: Player-specific `TakeDamageModifierDifference`.

### 1.3 Expected Players Scaling ($S_{exp}$)

If the number of players nearby a boss is less than the configured `ExpectedPlayers`, incoming damage is increased to compensate for the lack of players (simulating the difficulty of a full party).

Condition: $N_{nearby} < N_{expected}$

$$
\begin{aligned}
\Delta N &= N_{expected} - N_{nearby} \\
M_{exp} &= K_{scale} \cdot (\Delta N)^2 + 1 \\
D_{taken} &\leftarrow D_{taken} \times M_{exp}
\end{aligned}
$$

Where:

* $N_{nearby}$: Count of active players within 500 tiles of the boss.
* $N_{expected}$: `ExpectedPlayers` config value.
* $K_{scale}$: `ScalingMultiplier` config value.

### 1.4 Equalize Deaths Mode ($M_{eq}$)

When `EqualizeDeathsMode` is enabled, an additional multiplier is applied to incoming damage to synchronize the difficulty across the party. This punishes players performing better than average (fewer deaths) or when the party is struggling (many dead).

$$
M_{eq} = F_{alive} \times F_{diff} \times F_{time}
$$

#### 1.4.1 Alive Factor ($F_{alive}$)

Increases damage when fewer players are alive.

$$
F_{alive} = 1 + 0.5 \cdot \left( \frac{N_{online} - N_{alive}}{N_{online}} \right)
$$

#### 1.4.2 Individual Difference Factor ($F_{diff}$)

Increases damage for players who have fewer deaths than the party average.

$$
\begin{aligned}
\mu_{deaths} &= \frac{N_{total\_deaths}}{N_{online}} \\
F_{diff} &= \max\left(1, 1 + 0.15 \cdot (\mu_{deaths} - N_{your\_deaths}) \right)
\end{aligned}
$$

#### 1.4.3 Time Factor ($F_{time}$)

Increases damage over time to prevent stalling.

$$
F_{time} = 1 + 0.0003 \cdot T_{seconds}^2
$$

### 1.5 High Health Damage Scaling ($S_{high}$)

When enabled, players take additional flat damage during boss fights if they remain above a health threshold for an extended period. This encourages active engagement and prevents players from staying at full health indefinitely.

$$
\begin{aligned}
T_{over} &= \max(0, T_{above} - T_{delay}) \\
D_{extra} &= \min(C_{max}, C_{rate} \cdot T_{over}) \\
D_{taken} &\leftarrow D_{taken} + D_{extra}
\end{aligned}
$$

Where:

* $T_{above}$: Time in seconds the player has been above the health threshold.
* $T_{delay}$: `HighHealthDelaySeconds` (default: 10s).
* $C_{rate}$: `HighHealthDamageIncreasePerSecond` (default: 1).
* $C_{max}$: `HighHealthDamageMaximum` (default: 100).

---

## 2. Boss Scaling Model

The boss scaling model adjusts the boss's defense and offense dynamically based on the fight's pace relative to an expected duration.

### 2.1 Pace Analysis ($P_{mod}$)

The mod calculates an "Ideal Time" based on the boss's current health percentage and compares it to the actual fight duration.

$$
\begin{aligned}
T_{ideal} &= T_{total} \cdot (1 - H_{pct}) \\
\Delta T &= \frac{T_{alive} - T_{ideal}}{60}
\end{aligned}
$$

If the pace difference $|\Delta T|$ exceeds the deadzone $T_{deadzone}$:

$$
\begin{aligned}
M_{pace} &= 1 + 1.0 \cdot (|\Delta T| - T_{deadzone})^2 \\
M_{pace} &= \min(M_{pace}, C_{max\_def})
\end{aligned}
$$

**Application:**

* **Too Slow ($\Delta T > 0$):** Boss offense increases.
  $$
  {boss\_in} \leftarrow D_{boss\_in} \times M_{pace}
  $$
* **Too Fast ($\Delta T < 0$):** Boss defense increases.
  $$
  {boss\_in} \leftarrow D_{boss\_in} / M_{pace}
  $$

Where:

* $T_{total}$: `ExpectedTotalMinutes` (converted to ticks).
* $H_{pct}$: Current health percentage ($0.0 - 1.0$).
* $T_{deadzone}$: `ExpectedTotalMinutes / 5`.

### 2.2 Weapon Adaptation ($W_{adapt}$)

The boss adapts to a specific Player/Weapon combination (a "Combo") if that combo contributes a disproportionately high amount of damage during a **10% HP phase**. The system uses an Exponential Moving Average (EMA) to track damage and applies reductions when a combo dominates while the fight is progressing faster than expected.

**Implementation Details:**

* **Combo Key:** Defined by $(\text{playerId}, \text{weaponKey})$.
* **Phase Damage ($D_{phase}^{(i,w)}$):** Damage recorded for combo $(i,w)$ during the current 10% HP phase.
* **Running Average ($\bar{D}_{run}^{(i,w)}$):** Maintained using EMA with a smoothing factor $\alpha$ (`PhaseAvgAlpha` = 0.4).

**EMA Update (on phase boundary):**

$$
\bar{D}_{run}^{(i,w)} \leftarrow (1-\alpha)\,\bar{D}_{run}^{(i,w)} + \alpha\,D_{phase}^{(i,w)}
$$

**Comparison to Others:**
The system compares a combo's running damage to the mean of all other running combos ($N$ is total running combos).

$$
\begin{aligned}
\bar{D}_{others}^{(i,w)} &= \frac{D_{total}^{run} - \bar{D}_{run}^{(i,w)}}{\max(1,\,N-1)} \\
R^{(i,w)} &= \frac{\bar{D}_{run}^{(i,w)}}{\bar{D}_{others}^{(i,w)}}
\end{aligned}
$$

**Adaptation Factor:**
If the damage ratio $R^{(i,w)}$ exceeds the `WeaponAdaptationCompleteMultiplier` AND the phase is considered "too fast," the combo's damage is reduced.

$$
\lambda = \max\big(\text{maxReduction},\,\min\big(1.0,\,\frac{\bar{D}_{others}^{(i,w)}}{\bar{D}_{run}^{(i,w)}}\big)\big)
$$

When a hit from a combo with $\lambda < 1$ occurs, hit damage is multiplied by $\lambda$.

**Weapon Key Encoding:**

* **Items:** `weaponKey = item.type + 1` (positive keys).
* **Projectiles:** If the owner's held item is valid, the held item key is used; otherwise projectiles use `-(projectile.type + 1)` (unique negative keys).
