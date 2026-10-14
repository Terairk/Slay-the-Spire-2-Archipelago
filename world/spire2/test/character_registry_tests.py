import copy
import unittest
from unittest.mock import patch

from BaseClasses import CollectionState
from Fill import distribute_items_restrictive
from Options import OptionError
from test.general import setup_multiworld, setup_solo_multiworld
from worlds.AutoWorld import call_all
from worlds.spire2 import SlayTheSpire2World
from worlds.spire2.characters import (
    CharacterDefinition, DEFAULT_STARTER_POWER, PREDEFINED_CHARACTERS, VANILLA_CHARACTERS,
    character_list, character_registry, create_character_registry, registered_character_blocks,
)
from worlds.spire2.constants import CHAR_OFFSET, NUM_CUSTOM
from worlds.spire2.items import base_item_table, create_item_tables, item_table
from worlds.spire2.locations import create_location_data, create_location_tables, location_table
from worlds.spire2.options import GoalNumChar, ModdedCharacters, PickNumberCharacters, UnlockedCharacter
from worlds.spire2.rules_new import STARTER_CARD_POWER, STARTER_RELIC_POWER, SpireHasPower


class TestCharacterRegistry(unittest.TestCase):
    def test_existing_vanilla_and_generic_ids_are_unchanged(self):
        # Freeze the published order independently of the registry implementation.
        original = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]
        original += [f"Custom Character {i}" for i in range(1, 6)]
        for block, name in enumerate(original, start=1):
            for suffix, data in base_item_table.items():
                expected = None if data.code is None else block * CHAR_OFFSET + data.code
                self.assertEqual(expected, item_table[f"{name} {suffix}"].code)
            for data in create_location_data():
                expected = None if data.id is None else (block - 1) * CHAR_OFFSET + data.id
                self.assertEqual(expected, location_table[f"{name} {data.name}"])

    def test_named_blocks_and_names_match_the_published_registry(self):
        expected = [
            ("The Tectonic", "INTOTHESPIREVERSE-SHADOW_IRONCLAD"),
            ("The Captain", "INTOTHESPIREVERSE-SHADOW_REGENT"),
            ("The Acolyte", "INTOTHESPIREVERSE-SHADOW_SILENT"),
            ("The Slime Boss", "SLIMEBOSS-SLIME_BOSS"),
            ("The Guardian", "GUARDIAN-GUARDIAN"),
            ("The Hexaghost", "HEXAGHOST-HEXAGHOST"),
            ("The Champ", "CHAMP-CHAMP"),
            ("The Automaton", "AUTOMATON-AUTOMATON"),
            ("The Snecko", "SNECKO-SNECKO"),
            ("The Hermit", "HERMIT-HERMIT"),
            ("The Awakened", "AWAKENED-AWAKENED"),
            ("Watcher", "WATCHER-WATCHER"),
        ]
        for block, (name, internal_id) in enumerate(expected, start=40):
            self.assertEqual((name, internal_id),
                             (character_registry[block].name, character_registry[block].internal_id))
            self.assertEqual(block * CHAR_OFFSET + 4, item_table[f"{name} Progressive Ancient"].code)
            self.assertEqual((block - 1) * CHAR_OFFSET + 79, location_table[f"{name} Potion Drop 1"])

    def test_new_vanilla_character_does_not_renumber_existing_tables(self):
        future = CharacterDefinition("Future Vanilla", "FUTURE_VANILLA", DEFAULT_STARTER_POWER)
        registry = create_character_registry((*VANILLA_CHARACTERS, future))
        self.assertEqual(future, registry[21])
        items, _, _ = create_item_tables(registry)
        locations, _, _ = create_location_tables(registry)
        self.assertEqual(item_table, {name: items[name] for name in item_table})
        self.assertEqual(location_table, {name: locations[name] for name in location_table})
        self.assertEqual(210004, items["Future Vanilla Progressive Ancient"].code)

    def test_ids_are_unique_and_stay_within_the_client_player_block(self):
        for ids in ([data.code for data in item_table.values() if data.code is not None],
                    [code for code in location_table.values() if code is not None]):
            self.assertEqual(len(ids), len(set(ids)))
            self.assertTrue(all(0 < code < 1_000_000 for code in ids))

    def test_starter_profiles_cover_exactly_every_registered_vanilla_character(self):
        self.assertEqual(set(character_list), set(STARTER_CARD_POWER))
        self.assertEqual(set(character_list), set(STARTER_RELIC_POWER))
        self.assertTrue(all(definition.starter_power is not None for definition in VANILLA_CHARACTERS),
                        "Every vanilla character must choose reviewed starter power or DEFAULT_STARTER_POWER")
        self.assertTrue(all(definition.starter_power is None for definition in PREDEFINED_CHARACTERS))
        self.assertEqual(len(character_list) + NUM_CUSTOM, GoalNumChar.range_end)
        self.assertEqual(len(character_list) + NUM_CUSTOM, PickNumberCharacters.range_end)
        for index, name in enumerate(character_list):
            self.assertEqual(index, UnlockedCharacter.from_any(name.lower()).value)


class TestCharacterRosters(unittest.TestCase):
    @staticmethod
    def generate(**options):
        return setup_multiworld(SlayTheSpire2World, seed=42, options={
            "characters": [], "lock_characters": 0, "potion_sanity": True, **options,
        }).worlds[1]

    def test_ordered_option_deduplicates_case_insensitively(self):
        self.assertEqual(["Z-Mod", "watcher-watcher", "A-Mod"],
                         ModdedCharacters.from_any([" Z-Mod ", "watcher-watcher", "z-MOD",
                                                    "A-Mod", "WATCHER-WATCHER"]).value)

    def test_basic_and_advanced_preserve_order_and_number_only_unknown_characters(self):
        roster = ["WATCHER-WATCHER", "Z-Mod", "GUARDIAN-GUARDIAN", "A-Mod"]
        for advanced in (False, True):
            world = self.generate(modded_characters=roster, use_advanced_characters=advanced,
                                  advanced_characters={name: {} for name in roster})
            self.assertEqual(["Watcher", "Custom Character 1", "The Guardian", "Custom Character 2"],
                             [config.name for config in world.characters])
            self.assertEqual([51, 6, 44, 7], [config.char_offset for config in world.characters])
            self.assertEqual(roster, [config.official_name for config in world.characters])

    def test_advanced_case_collisions_are_rejected(self):
        with self.assertRaisesRegex(OptionError, "duplicate character IDs"):
            self.generate(use_advanced_characters=True,
                          advanced_characters={"WATCHER-WATCHER": {}, "watcher-watcher": {}})

    def test_random_subset_has_contiguous_generic_numbers_in_original_relative_order(self):
        roster = [f"Mod-{i:02}" for i in reversed(range(25))]
        world = self.generate(modded_characters=roster, pick_num_characters=4)
        self.assertEqual([f"Custom Character {i}" for i in range(1, 5)],
                         [config.name for config in world.characters])
        selected = {config.option_name for config in world.characters}
        self.assertEqual([name for name in roster if name in selected],
                         [config.option_name for config in world.characters])
        fixed = self.generate(modded_characters=roster, pick_num_characters=1,
                              lock_characters=2, unlocked_character=roster[-1])
        self.assertEqual(("Custom Character 1", roster[-1]),
                         (fixed.characters[0].name, fixed.characters[0].official_name))

    def test_fifteen_modded_characters_and_vanilla_can_generate_and_fill(self):
        roster = [definition.internal_id for definition in PREDEFINED_CHARACTERS] + ["ModA", "ModB", "ModC"]
        for new_logic in (False, True):
            with self.subTest(new_logic=new_logic):
                world = self.generate(characters=character_list, modded_characters=roster,
                                      use_new_logic=new_logic)
                self.assertEqual(len(character_list) + 15, len(world.characters))
                self.assertEqual(15, world.modded_num)
                distribute_items_restrictive(world.multiworld, panic_method="raise")
                self.assertFalse(world.multiworld.get_unfilled_locations())
                self.assertTrue(world.multiworld.can_beat_game())

    def test_all_fifteen_generic_slots_and_overflow(self):
        roster = [f"Unknown-{i}" for i in range(16)]
        world = self.generate(modded_characters=roster[:15])
        self.assertEqual(20, world.characters[-1].char_offset)
        self.assertEqual("Custom Character 15", world.characters[-1].name)
        with self.assertRaisesRegex(OptionError, "at most 15"):
            self.generate(modded_characters=roster)
        with self.assertRaisesRegex(OptionError, "at most 15"):
            self.generate(modded_characters=roster, pick_num_characters=16)

    def test_random_selection_replaces_excess_mods_without_shrinking_or_losing_fixed_start(self):
        roster = [f"Unknown-{i}" for i in range(30)]
        world = self.generate(characters=character_list, modded_characters=roster,
                              pick_num_characters=20, lock_characters=2, unlocked_character=roster[-1])
        self.assertEqual(20, len(world.characters))
        self.assertEqual(15, world.modded_num)
        self.assertEqual([roster[-1]], [c.option_name for c in world.characters if not c.locked])

    def test_two_players_can_use_different_order_without_changing_named_ids(self):
        mw = setup_multiworld([SlayTheSpire2World] * 2, seed=42, options=[
            {"characters": [], "modded_characters": roster, "lock_characters": 0, "potion_sanity": True}
            for roster in (["WATCHER-WATCHER", "ModA"], ["GUARDIAN-GUARDIAN", "ModB", "WATCHER-WATCHER"])
        ])
        self.assertEqual(mw.worlds[1].get_location("Watcher Potion Drop 1").address,
                         mw.worlds[2].get_location("Watcher Potion Drop 1").address)
        distribute_items_restrictive(mw, panic_method="raise")
        self.assertTrue(mw.can_beat_game())

    def test_tracker_restores_new_and_legacy_generic_assignments(self):
        for legacy in (False, True):
            # Model the old APWorld's assignment of Watcher to generic block 6.
            registered = dict(registered_character_blocks)
            if legacy:
                registered.pop("watcher-watcher")
            with patch.dict(registered_character_blocks, registered, clear=True):
                original = self.generate(modded_characters=["WATCHER-WATCHER", "ModA"])
            data = copy.deepcopy(original.fill_slot_data())
            regenerated = setup_solo_multiworld(SlayTheSpire2World, steps=())
            regenerated.re_gen_passthrough = {SlayTheSpire2World.game: data}
            for step in ("generate_early", "create_regions", "create_items", "set_rules"):
                call_all(regenerated, step)
            restored = regenerated.worlds[1]
            self.assertEqual(data["characters"], restored.fill_slot_data()["characters"])
            self.assertEqual({(loc.name, loc.address) for loc in original.get_locations()},
                             {(loc.name, loc.address) for loc in restored.get_locations()})
            self.assertEqual([(item.name, item.code) for item in original.multiworld.itempool
                              if item.advancement],
                             [(item.name, item.code) for item in regenerated.itempool if item.advancement])
            first = restored.characters[0]
            location = restored.get_location(f"{first.name} Potion Drop 1")
            if legacy:
                self.assertEqual("WATCHER-WATCHER Potion Drop 1", restored.location_id_to_alias[location.address])
                self.assertEqual(6, first.char_offset)
            else:
                self.assertNotIn(location.address, restored.location_id_to_alias)
                self.assertEqual("Watcher", first.name)

    def test_default_starter_profile_restores_baseline_then_adds_four_power(self):
        # Missing reviewed data uses the explicit fallback; coverage tests above still fail
        # if a real registered vanilla character has not chosen a profile.
        with patch.dict(STARTER_CARD_POWER, {}, clear=True), patch.dict(STARTER_RELIC_POWER, {}, clear=True):
            world = self.generate(characters=["Ironclad"], progressive_starter_card=True,
                                  progressive_starter_relic=True)
            rule = SpireHasPower(world.characters[0].char_offset, 9).resolve(world)
        for kind, penalty in (("Card", 1.0), ("Relic", 2.5)):
            state = CollectionState(world.multiworld)
            before = rule.strength(state)
            state.collect(world.create_item(f"Ironclad Progressive Starter {kind}"), prevent_sweep=True)
            first = rule.strength(state)
            self.assertEqual(penalty, before[2] - first[2])
            self.assertEqual(before[:2], first[:2])
            state.collect(world.create_item(f"Ironclad Progressive Starter {kind}"), prevent_sweep=True)
            second = rule.strength(state)
            self.assertEqual(4.0, second[1] - first[1])
            self.assertEqual(first[2], second[2])
            state.collect(world.create_item(f"Ironclad Progressive Starter {kind}"), prevent_sweep=True)
            self.assertEqual(second, rule.strength(state))
