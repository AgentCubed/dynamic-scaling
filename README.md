# Dynamic Scaling

A refactored version of my [original mod](https://github.com/AgentCubed/dynamic-scaling). It attempts to dynamically determine the difficulty of bosses at runtime as well as many damage configs.

## 1. Player Scaling Model

The player scaling model adjusts the damage players deal and take based on configuration, performance (deaths), and server population.

### 1.1 Damage Output Scaling ($D_{out}$)

The damage a player deals to NPCs is modified by a multiplier derived from a base configuration and player-specific overrides.

$$
M_{deal} = 1.2^{(C_{deal} + \delta_{deal})}
$$

$$
D_{final} = D_{initial} \times M_{deal}
$$

Where:

-   $C_{deal}$ is the global `DealDamage` configuration value.
-   $\delta_{deal}$ is the player-specific `DealDamageModifierDifference` from `PlayerOverrides`.

### 1.2 Damage Intake Scaling ($D_{in}$)

The damage a player takes from NPCs is similarly modified, but deaths have a double weight to punish repeated failure more severely.

$$
M_{take} = 1.2^{(C_{take} + 2 \cdot N_{deaths} + \delta_{take})}
$$

$$
D_{taken} = D_{raw} \times M_{take}
$$

Where:

-   $C_{take}$ is the global `TakeDamage` configuration value.
-   $\delta_{take}$ is the player-specific `TakeDamageModifierDifference`.

### 1.3 Expected Players Scaling ($S_{exp}$)

If the number of players nearby a boss is less than the configured `ExpectedPlayers`, incoming damage is increased to compensate for the lack of players (simulating the difficulty of a full party).

If $N_{nearby} < N_{expected}$:

$$
\Delta N = N_{expected} - N_{nearby}
$$

$$
M_{exp} = K_{scale} \cdot (\Delta N)^2 + 1
$$

$$
D_{taken} \leftarrow D_{taken} \times M_{exp}
$$

Where:

-   $N_{nearby}$ is the count of active players within 300 tiles of the boss.
-   $N_{expected}$ is the `ExpectedPlayers` config value.
-   $K_{scale}$ is the `ScalingMultiplier` config value.

### 1.4 Equalize Deaths Mode ($M_{eq}$)

When `EqualizeDeathsMode` is enabled, an additional multiplier is applied to incoming damage to synchronize the difficulty across the party, punishing players who are performing better than average (fewer deaths) or when the party is struggling (many dead).

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
\mu_{deaths} = \frac{N_{total\_deaths}}{N_{online}}
$$

$$
F_{diff} = \max\left(1, 1 + 0.15 \cdot (\mu_{deaths} - N_{your\_deaths}) \right)
$$

#### 1.4.3 Time Factor ($F_{time}$)

Increases damage over time to prevent stalling.

$$
F_{time} = 1 + 0.0003 \cdot T_{seconds}^2
$$

### 1.5 High Health Damage Scaling ($S_{high}$)

When enabled, players take additional flat damage during boss fights if they remain above a health threshold for an extended period. This encourages active engagement and prevents players from staying at full health indefinitely.

After a configurable delay, players begin taking increasing flat damage per second, up to a maximum cap.

$$
T_{over} = \max(0, T_{above} - T_{delay})
$$

$$
D_{extra} = \min(C_{max}, C_{rate} \cdot T_{over})
$$

$$
D_{taken} \leftarrow D_{taken} + D_{extra}
$$

Where:

-   $T_{above}$ is the time in seconds the player has been above the health threshold.
-   $T_{delay}$ is `HighHealthDelaySeconds` (default: 10 seconds).
-   $C_{rate}$ is `HighHealthDamageIncreasePerSecond` (default: 1 damage/second).
-   $C_{max}$ is `HighHealthDamageMaximum` (default: 100 damage).
-   Only applies during active boss fights when players are within range of bosses.

---

## 2. Boss Scaling Model

## 2. Boss Scaling Model

The boss scaling model adjusts the boss's defense and offense dynamically based on the fight's pace relative to an expected duration.

### 2.1 Pace Analysis ($P_{mod}$)

The mod calculates an "Ideal Time" based on the boss's current health percentage.

$$
T_{ideal} = T_{total} \cdot (1 - H_{pct})
$$

The difference between the actual time alive and the ideal time is calculated in minutes.

$$
\Delta T = \frac{T_{alive} - T_{ideal}}{60}
$$

A deadzone is applied where no scaling occurs if the pace is close to ideal.

If $|\Delta T| > T_{deadzone}$:

$$
M_{pace} = 1 + 1.0 \cdot (|\Delta T| - T_{deadzone})^2
$$

$$
M_{pace} = \min(M_{pace}, C_{max\_def})
$$

#### Application:

-   **Too Slow ($\Delta T > 0$)**: The boss is dying too slowly. Increase boss offense (boss takes more damage).
    $$
    D_{boss\_in} \leftarrow D_{boss\_in} \times M_{pace}
    $$
-   **Too Fast ($\Delta T < 0$)**: The boss is dying too quickly. Increase boss defense (boss takes less damage).
    $$
    D_{boss\_in} \leftarrow D_{boss\_in} / M_{pace}
    $$

Where:

-   $T_{total}$ is `ExpectedTotalMinutes` (converted to ticks).
-   $H_{pct}$ is the current health percentage ($0.0 - 1.0$).
-   $T_{deadzone}$ is `ExpectedTotalMinutes / 5`.

### 2.2 Weapon Adaptation ($W_{adapt}$)

The mod tracks damage from specific player-weapon combinations to prevent one strategy from dominating.

#### Running Average Calculation

$$
D_{running} = (1 - \alpha) \cdot D_{prev} + \alpha \cdot D_{phase}
$$

Where $\alpha = 0.4$.

#### Comparison to Mean

The damage of a specific combo is compared to the mean of all other combos.

$$
\mu_{others} = \frac{\sum D_{running} - D_{combo}}{N_{combos} - 1}
$$

$$
R_{ratio} = \frac{D_{combo}}{\mu_{others}}
$$

#### Adaptation Factor

If $R_{ratio} \ge K_{complete}$ AND the boss is currently scaling defense (dying too fast):

$$
F_{adapt} = \max\left(K_{max\_red}, \min\left(1, \frac{\mu_{others}}{D_{combo}}\right)\right)
$$

$$
D_{boss\_in} \leftarrow D_{boss\_in} \times F_{adapt}
$$

Where:

-   $K_{complete}$ is `WeaponAdaptationCompleteMultiplier`.
-   $K_{max\_red}$ is `WeaponAdaptationMaxReduction`.
