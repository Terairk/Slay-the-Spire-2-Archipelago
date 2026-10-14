from dataclasses import dataclass
from typing import List, Any, Sequence

from .constants import (NUM_CUSTOM, ORIGINAL_VANILLA_BLOCKS, GENERIC_CHARACTER_BLOCKS,
                        FUTURE_VANILLA_BLOCKS, PREDEFINED_CHARACTER_BLOCKS)


@dataclass(frozen=True)
class StarterPower:
    # Missing-starter penalty, then second-receipt bonus above the normal loadout.
    card: tuple[float, float] = (1.0, 4.0)
    relic: tuple[float, float] = (2.5, 4.0)


DEFAULT_STARTER_POWER = StarterPower()


@dataclass(frozen=True)
class CharacterDefinition:
    name: str
    internal_id: str | None
    # Vanilla entries explicitly choose reviewed values or DEFAULT_STARTER_POWER.
    # Modded entries have no assumed power credit for unverified starter upgrades.
    starter_power: StarterPower | None = None


# Append only: position within each list determines its permanent ID block.
VANILLA_CHARACTERS = (
    CharacterDefinition("Ironclad", "Ironclad", StarterPower((1.0, 4.5), (3.0, 2.5))),
    CharacterDefinition("Silent", "Silent", StarterPower((1.5, 4.5), (2.0, 3.5))),
    CharacterDefinition("Defect", "Defect", StarterPower((1.0, 3.0), (1.0, 3.0))),
    CharacterDefinition("Regent", "Regent", StarterPower((1.5, 5.0), (1.5, 2.5))),
    CharacterDefinition("Necrobinder", "Necrobinder", StarterPower((1.0, 2.5), (4.0, 5.0))),
)

PREDEFINED_CHARACTERS = (
    CharacterDefinition("The Tectonic", "INTOTHESPIREVERSE-SHADOW_IRONCLAD"),
    CharacterDefinition("The Captain", "INTOTHESPIREVERSE-SHADOW_REGENT"),
    CharacterDefinition("The Acolyte", "INTOTHESPIREVERSE-SHADOW_SILENT"),
    CharacterDefinition("The Slime Boss", "SLIMEBOSS-SLIME_BOSS"),
    CharacterDefinition("The Guardian", "GUARDIAN-GUARDIAN"),
    CharacterDefinition("The Hexaghost", "HEXAGHOST-HEXAGHOST"),
    CharacterDefinition("The Champ", "CHAMP-CHAMP"),
    CharacterDefinition("The Automaton", "AUTOMATON-AUTOMATON"),
    CharacterDefinition("The Snecko", "SNECKO-SNECKO"),
    CharacterDefinition("The Hermit", "HERMIT-HERMIT"),
    CharacterDefinition("The Awakened", "AWAKENED-AWAKENED"),
    CharacterDefinition("Watcher", "WATCHER-WATCHER"),
)


def create_character_registry(
        vanilla: Sequence[CharacterDefinition] = VANILLA_CHARACTERS,
        predefined: Sequence[CharacterDefinition] = PREDEFINED_CHARACTERS,
) -> dict[int, CharacterDefinition]:
    vanilla_blocks = (*ORIGINAL_VANILLA_BLOCKS, *FUTURE_VANILLA_BLOCKS)
    if len(vanilla) > len(vanilla_blocks) or len(predefined) > len(PREDEFINED_CHARACTER_BLOCKS):
        raise ValueError("Character registry exceeds its reserved ID range")
    if NUM_CUSTOM > len(GENERIC_CHARACTER_BLOCKS):
        raise ValueError("Custom character limit exceeds its reserved ID range")
    registry = dict(zip(vanilla_blocks, vanilla))
    registry.update({block: CharacterDefinition(f"Custom Character {index}", None)
                     for index, block in enumerate(GENERIC_CHARACTER_BLOCKS[:NUM_CUSTOM], start=1)})
    registry.update(zip(PREDEFINED_CHARACTER_BLOCKS, predefined))
    names = [definition.name for definition in registry.values()]
    internal_ids = [definition.internal_id.casefold() for definition in registry.values()
                    if definition.internal_id is not None]
    if len(set(names)) != len(names) or len(set(internal_ids)) != len(internal_ids):
        raise ValueError("Character registry contains duplicate names or internal IDs")
    return registry


character_registry = create_character_registry()
character_list = [definition.name for definition in VANILLA_CHARACTERS]
vanilla_by_name = {definition.name: definition for definition in VANILLA_CHARACTERS}
character_offset_map = {
    name.casefold(): block
    for block, definition in character_registry.items()
    if definition.name in vanilla_by_name
    for name in (definition.name, definition.internal_id)
}
registered_character_blocks = {
    definition.internal_id.casefold(): block
    for block, definition in character_registry.items() if definition.internal_id is not None
}


def unique_character_ids(values: Sequence[str]) -> list[str]:
    """Keep the first occurrence, using the client's case-insensitive identity rules."""
    result = []
    seen = set()
    for value in values:
        if not isinstance(value, str) or not value.strip():
            raise ValueError("Character IDs must be non-empty strings")
        value = value.strip()
        if value.casefold() not in seen:
            result.append(value)
            seen.add(value.casefold())
    return result


class CharacterConfig:

    def __init__(self, name: str, option_name: str, char_offset: int, mod_num: int, seed: str, locked: bool, **kwargs):
        self.name: str = name
        self.option_name: str = option_name
        self.mod_num = mod_num
        self.char_offset: int = char_offset
        if self.mod_num == 0:
            self.official_name: str = vanilla_by_name[name].internal_id
        else:
            definition = character_registry.get(char_offset)
            self.official_name = (definition.internal_id if definition is not None
                                  and definition.name == name and definition.internal_id is not None
                                  else option_name)
        self.seed: str = seed
        self.locked: bool = locked
        self.progressive_starter_card: bool = bool(kwargs.get('progressive_starter_card', False))
        self.progressive_starter_relic: bool = bool(kwargs.get('progressive_starter_relic', False))
        self.ascension: List[str] = kwargs['ascension']
        # Doesn't need to make it to the mod
        if 'ascension_down' in kwargs:
            self.ascension_down: List[str] = kwargs['ascension_down']
        else:
            self.ascension_down = []

    def to_dict(self) -> dict[str, Any]:
        return {
            'name': self.name,
            'option_name': self.option_name,
            'char_offset': self.char_offset,
            'official_name': self.official_name,
            'seed': self.seed,
            'locked': self.locked,
            'mod_num': self.mod_num,
            'ascension': self.ascension,
            'ascension_down': self.ascension_down,
            'progressive_starter_card': self.progressive_starter_card,
            'progressive_starter_relic': self.progressive_starter_relic,
        }

    def __repr__(self):
        return self.to_dict().__repr__()
