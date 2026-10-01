import unittest

from BaseClasses import CollectionState
from test.general import setup_multiworld
from worlds.spire2 import SlayTheSpire2World
from worlds.spire2.rules import SpireHasPower


class LogicTests(unittest.TestCase):
    # Test behaviour, not carefully balanced inventories that change whenever weights do.
    options = {
        "characters": ["Silent"],
        "shuffle_all_cards": True,
        "campfire_sanity": True,
        "neow_sanity": True,
        "shop_sanity": True,
        "shop_card_slots": 5,
        "shop_neutral_card_slots": 2,
        "shop_relic_slots": 3,
        "shop_potion_slots": 3,
        "shop_remove_slots": True,
    }
    shop_slots = (
        ("shop_card_slots", "Shop Card Slot", 5),
        ("shop_neutral_card_slots", "Neutral Shop Card Slot", 2),
        ("shop_relic_slots", "Shop Relic Slot", 3),
        ("shop_potion_slots", "Shop Potion Slot", 3),
    )

    def make_world(self, **options):
        return setup_multiworld(SlayTheSpire2World, seed=42,
                                options={**self.options, **options}).worlds[1]

    def power_rule(self, world, act=1):
        return SpireHasPower(world.characters[0].char_offset, 9, act=act,
                             rest=True, smith=True, remove=True, shop=True).resolve(world)

    def test_first_smith_is_required_only_with_campfire_sanity(self):
        for enabled in (False, True):
            with self.subTest(campfire_sanity=enabled):
                world = self.make_world(campfire_sanity=enabled)
                state = CollectionState(world.multiworld)
                for item in world.multiworld.get_items():
                    if item.name != "Silent Progressive Smith":
                        state.collect(item, prevent_sweep=True)
                boss_rule = world.get_entrance("Silent Act 1 Boss Arena").access_rule
                self.assertEqual(not enabled, boss_rule(state))
                if enabled:
                    state.collect(world.create_item("Silent Progressive Smith"), prevent_sweep=True)
                    self.assertTrue(boss_rule(state))

    def test_extra_power_can_replace_optional_support(self):
        world = self.make_world()
        state = CollectionState(world.multiworld)
        state.collect(world.create_item("Silent Progressive Smith"), prevent_sweep=True)
        bosses = [world.get_entrance(f"Silent Act {act} Boss Arena") for act in (1, 2, 3)]
        for boss in bosses:
            self.assertFalse(boss.access_rule(state))
        # Deliberately ample power: this checks for hidden hard gates, not balance.
        for _ in range(100):
            state.collect(world.create_item("Silent Card Reward"), prevent_sweep=True)
        for boss in bosses:
            with self.subTest(boss=boss.name):
                self.assertTrue(boss.access_rule(state))

    def test_unshuffled_slots_count_as_available(self):
        world = self.make_world()
        state = CollectionState(world.multiworld)
        for _, item, _ in self.shop_slots:
            state.collect(world.create_item(f"Silent {item}"), prevent_sweep=True)
        partial_world = self.make_world(**{option: total - 1 for option, _, total in self.shop_slots})
        partial_state = CollectionState(partial_world.multiworld)
        self.assertGreater(self.power_rule(world).shop_penalty(state), 0)
        self.assertEqual(self.power_rule(world).shop_penalty(state),
                         self.power_rule(partial_world).shop_penalty(partial_state))

    def test_restoring_shop_slots_reduces_penalty_to_zero(self):
        world = self.make_world()
        state = CollectionState(world.multiworld)
        rules = [self.power_rule(world, act) for act in (1, 2, 3)]
        penalties = [rule.shop_penalty(state) for rule in rules]
        self.assertGreater(penalties[0], penalties[1])
        self.assertGreater(penalties[1], penalties[2])
        self.assertGreater(penalties[2], 0)
        for _, item, count in self.shop_slots:
            for tier in range(1, count + 1):
                with self.subTest(item=item, tier=tier):
                    state.collect(world.create_item(f"Silent {item}"), prevent_sweep=True)
                    updated = [rule.shop_penalty(state) for rule in rules]
                    for before, after in zip(penalties, updated):
                        self.assertLess(after, before)
                    penalties = updated
        self.assertEqual([0, 0, 0], penalties)

    def test_smith_and_removal_tiers_only_help_from_their_act(self):
        world = self.make_world()
        rules = [self.power_rule(world, act) for act in (1, 2, 3)]
        for item in ("Progressive Smith", "Progressive Shop Remove"):
            state = CollectionState(world.multiworld)
            for tier in (1, 2, 3):
                before = [rule.strength(state)[2] for rule in rules]
                state.collect(world.create_item(f"Silent {item}"), prevent_sweep=True)
                for act, rule in enumerate(rules, start=1):
                    with self.subTest(item=item, tier=tier, act=act):
                        after = rule.strength(state)[2]
                        if act < tier:
                            self.assertEqual(before[act - 1], after)
                        else:
                            self.assertLess(after, before[act - 1])

    def test_difficulty_orders_power_requirements(self):
        requirements = []
        for difficulty in ("easy", "normal", "hard"):
            world = self.make_world(logic_difficulty=difficulty)
            requirements.append(self.power_rule(world).strength(CollectionState(world.multiworld))[2])
        self.assertGreater(requirements[0], requirements[1])
        self.assertGreater(requirements[1], requirements[2])
