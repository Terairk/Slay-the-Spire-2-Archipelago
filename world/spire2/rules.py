import dataclasses
import typing
from typing import TYPE_CHECKING, List

from BaseClasses import CollectionState
from NetUtils import JSONMessagePart
from rule_builder.options import OptionFilter
from rule_builder.rules import Rule, True_, Has, HasFromListUnique
from .characters import CharacterConfig
from .options import CampfireSanity, GoldSanity, ProgressiveStarterCard, ProgressiveStarterRelic

if TYPE_CHECKING:
    from .world import SlayTheSpire2World


# First useful choices matter more than a full page. Values are marginal per slot;
# colored-card/relic choice contributes 1.5 each, neutral 0.5, potions 1 altogether.
# The client restores entries from the end: first available is Power/rare neutral.
SHOP_SLOT_VALUES = (
    ("Shop Card Slot", 5, (0.625, 0.375, 0.25, 0.125, 0.125)),
    ("Neutral Shop Card Slot", 2, (0.375, 0.125)),
    ("Shop Relic Slot", 3, (0.75, 0.5, 0.25)),
    ("Shop Potion Slot", 3, (0.5, 0.375, 0.125)),
)


@dataclasses.dataclass()
class SpireHasPower(Rule['SlayTheSpire2World'], game="Slay the Spire II"):
    char_offset: int
    power_level: float
    card_rewards: int = 0
    act: int = 1
    minimum_cards: float = 0
    rest: bool = False
    smith: bool = False
    remove: bool = False
    shop: bool = False

    @typing.override
    def _instantiate(self, world: 'SlayTheSpire2World') -> Rule.Resolved:
        config = next(config for config in world.characters if config.char_offset == self.char_offset)
        options = world.options
        return self.Resolved(
            char=config.name,
            power_level=options.logic_difficulty.combat_requirement(self.power_level),
            vanilla_cards=0 if options.shuffle_all_cards else self.card_rewards / 2,
            minimum_cards=self.minimum_cards,
            rest_level=self.act if self.rest and options.campfire_sanity else 0,
            smith_level=(self.act if self.smith else self.act - 1) if options.campfire_sanity else 0,
            remove_level=(self.act if self.remove else self.act - 1)
                         if options.shop_sanity and options.shop_remove_slots else 0,
            act=self.act,
            neow_sanity=bool(options.neow_sanity),
            ancient_anytime=bool(options.ancient_relic_location),
            shop_scale=(1, 0.5, 0.25)[self.act - 1] if self.shop and options.shop_sanity else 0,
            shuffled_shop_slots=(options.shop_card_slots.value, options.shop_neutral_card_slots.value,
                                 options.shop_relic_slots.value, options.shop_potion_slots.value),
            # The APWorld cannot verify a modded character's Orobas mappings. Do not assume
            # those receipts grant upgrades when the client may leave its starters unchanged.
            starter_card=config.mod_num == 0 and bool(options.progressive_starter_card),
            starter_relic=config.mod_num == 0 and bool(options.progressive_starter_relic),
            player=world.player,
        )

    class Resolved(Rule.Resolved):
        char: str
        power_level: float
        vanilla_cards: float
        minimum_cards: float
        rest_level: int
        smith_level: int
        remove_level: int
        act: int
        neow_sanity: bool
        ancient_anytime: bool
        shop_scale: float
        shuffled_shop_slots: tuple[int, int, int, int]
        starter_card: bool
        starter_relic: bool

        def strength(self, state: CollectionState) -> tuple[float, float, float]:
            cards = (state.count(f"{self.char} Card Reward", self.player)
                     + 1.5 * state.count(f"{self.char} Rare Card Reward", self.player)
                     + self.vanilla_cards)
            if self.starter_card and state.has(f"{self.char} Progressive Starter Card", self.player, 2):
                cards += 2.5
            # Credit receipts immediately, as in the previous APWorld. Claim timing
            # remains a client setting rather than an additional logic restriction.
            relics = state.count(f"{self.char} Relic", self.player)
            power = cards + 1.5 * relics
            if self.starter_relic and state.has(f"{self.char} Progressive Starter Relic", self.player, 2):
                power += 2.5

            required = self.power_level
            if self.rest_level and not state.has(f"{self.char} Progressive Rest", self.player, self.rest_level):
                required += 4.5

            # earlier progressive smith's matter way more, act 3 one is weaker
            # for progressive shop remove's - early on it doesn't matter but later on
            # it matters more? its sort of unclear how to do shop remove's here
            # its all vibe based anyway
            for item, level, weights in (
                ("Progressive Smith", self.smith_level, (3, 2, 1)),
                ("Progressive Shop Remove", self.remove_level, (1, 2, 2)),
            ):
                received = state.count(f"{self.char} {item}", self.player)
                required += sum(weights[received:level])

            # instead of ancients being fixed and necessary for an act, spice things up
            # and make them give more power
            # anytime mode can provide later rewards early; encounter mode cannot.
            ancient_weights = (2.5, 4, 4)
            received = state.count(f"{self.char} Progressive Ancient", self.player) + int(not self.neow_sanity)
            claimable = min(3 if self.ancient_anytime else self.act, received)
            required += sum(ancient_weights[:self.act]) - sum(ancient_weights[:claimable])
            required += self.shop_penalty(state)
            return cards, power, required

        def shop_penalty(self, state: CollectionState) -> float:
            if not self.shop_scale:
                return 0
            missing = 0.0

            # later missing stuff matters less than the initial amounts
            for (item, total, values), shuffled in zip(SHOP_SLOT_VALUES, self.shuffled_shop_slots):
                restored = min(state.count(f"{self.char} {item}", self.player), shuffled)
                available = total - shuffled + restored
                missing += sum(values[available:])
            return missing * self.shop_scale

        @typing.override
        def _evaluate(self, state: CollectionState) -> bool:
            cards, power, required = self.strength(state)
            return cards >= self.minimum_cards and power >= required

        @typing.override
        def explain_json(self, state: CollectionState | None = None) -> List[JSONMessagePart]:
            description = (f"{self.char} requires power {self.power_level} and card strength "
                           f"{self.minimum_cards}; missing current Rest adds 4.5, "
                           "missing Smith tiers add 3/2/1, removal tiers add 1/2/2 "
                           "as their acts become available; Ancient support adjusts the threshold "
                           "by 2.5/4/4 relative to this act's expected rewards; "
                           f"missing shop choice adds up to {4.5 * self.shop_scale} power; "
                           f"free-card credit {self.vanilla_cards}, received relics count as 1.5 each")
            if state is not None:
                cards, power, required = self.strength(state)
                description += (f" (currently power {power}/{required}, cards {cards}/{self.minimum_cards}, "
                                f"shop penalty {self.shop_penalty(state)})")
            return [{"type": "text", "text": description}]


@dataclasses.dataclass()
class SpireHasGold(Rule['SlayTheSpire2World'], game="Slay the Spire II"):
    char: str
    gold: int

    @typing.override
    def _instantiate(self, world: 'SlayTheSpire2World') -> Rule.Resolved:
        if self.gold <= 0:
            return True_().resolve(world)
        return self.Resolved(self.gold, self.char, player=world.player)

    class Resolved(Rule.Resolved):
        gold: int
        char: str

        @typing.override
        def _evaluate(self, state: CollectionState) -> bool:
            # Keep the conservative Poverty allowance. Combat/filler gold is not progression.
            return (state.count(f"{self.char} Elite Gold", self.player) * 30
                    + state.count(f"{self.char} Boss Gold", self.player) * 75 >= self.gold)


def set_rules(world: 'SlayTheSpire2World') -> None:
    for config in world.characters:
        _set_rules(world, config)
    num_goals = world.options.num_chars_goal.value or len(world.characters)
    assert num_goals > 0
    world.set_completion_rule(HasFromListUnique(
        *[f"{config.name} Victory" for config in world.characters], count=num_goals))


def _set_rules(world: 'SlayTheSpire2World', config: CharacterConfig) -> None:
    prefix = config.name
    offset = config.char_offset
    gold_filter = dict(options=[OptionFilter(GoldSanity, 1)], filtered_resolution=True)
    world.set_rule(world.get_entrance(f"{prefix} Early Act 1"), Has(f"{prefix} Unlock"))

    # Budget roughly seven combat rewards per act: three before Mid, five before Late,
    # seven before the boss. Half shuffle leaves half of those rewards as free cards.
    world.set_rule(world.get_entrance(f"{prefix} Mid Act 1"),
                   SpireHasPower(offset, 3, card_rewards=3, minimum_cards=1, rest=True))
    world.set_rule(world.get_entrance(f"{prefix} Late Act 1"),
                   SpireHasPower(offset, 6, card_rewards=5, minimum_cards=1, rest=True, shop=True)
                   & Has(f"{prefix} Progressive Starter Relic", options=[OptionFilter(ProgressiveStarterRelic, 1)], filtered_resolution=True)
                   & Has(f"{prefix} Progressive Starter Card", options=[OptionFilter(ProgressiveStarterCard, 1)], filtered_resolution=True))
    world.set_rule(world.get_entrance(f"{prefix} Act 1 Boss Arena"),
                   SpireHasPower(offset, 9, card_rewards=7, minimum_cards=3,
                                 rest=True, smith=True, remove=True, shop=True)
                   & Has(f"{prefix} Progressive Smith", options=[OptionFilter(CampfireSanity, 1)], filtered_resolution=True))

    world.set_rule(world.get_entrance(f"{prefix} Early Act 2"),
                   SpireHasPower(offset, 10, card_rewards=7, act=2, minimum_cards=3, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Mid Act 2"),
                   SpireHasPower(offset, 12, card_rewards=10, act=2, minimum_cards=3, rest=True, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Late Act 2"),
                   SpireHasPower(offset, 15, card_rewards=12, act=2, minimum_cards=3, rest=True, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Act 2 Boss Arena"),
                   SpireHasPower(offset, 18, card_rewards=14, act=2, minimum_cards=5,
                                 rest=True, smith=True, remove=True, shop=True))

    world.set_rule(world.get_entrance(f"{prefix} Early Act 3"),
                   SpireHasPower(offset, 19, card_rewards=14, act=3, minimum_cards=5, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Mid Act 3"),
                   SpireHasPower(offset, 21, card_rewards=17, act=3, minimum_cards=5, rest=True, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Late Act 3"),
                   SpireHasPower(offset, 24, card_rewards=19, act=3, minimum_cards=5, rest=True, shop=True))
    world.set_rule(world.get_entrance(f"{prefix} Act 3 Boss Arena"),
                   SpireHasPower(offset, 27, card_rewards=21, act=3, minimum_cards=7,
                                 rest=True, smith=True, remove=True, shop=True))

    if world.options.shop_sanity:
        for act, purchase_gold in enumerate((50, 150, 270), start=1):
            world.set_rule(world.get_entrance(f"{prefix} Act {act} Shop"),
                           SpireHasGold(prefix, purchase_gold, **gold_filter))
