"""Regression for authored quantities accidentally matching inside other words."""
import unittest
from game_lessons import slot


class AuthoredSpans(unittest.TestCase):
    def test_quoted_quantity_is_a_whole_word(self):
        text='SOMEONE SAID "BUY ONE ROPE"'
        actual=slot(text,'ONE','QUANTITY')
        self.assertEqual(actual['start'],text.index('ONE ROPE'))
        self.assertEqual(text[actual['start']:actual['start']+actual['length']],'ONE')

    def test_ambiguous_and_embedded_values_are_rejected(self):
        for text in ('SOMEONE SAID BUY ROPE','BUY ONE ROPE AND ONE POTION'):
            with self.assertRaises(ValueError):slot(text,'ONE','QUANTITY')


if __name__=='__main__':unittest.main()
